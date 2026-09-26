using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Configlue;

/// <summary>Resolves and saves a generated configuration model over a set of state sources.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
public sealed class ConfiglueOptions<TModel, TFragment> : IWritableOptions<TModel>, IDisposable, IAsyncDisposable
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly StateSourceSet<TFragment> _sourceSet;
    private readonly StateWriteRoute _writeRoute;
    private readonly StateSchemaMigrationChain<TFragment> _migrationChain;
    private readonly IConfiglueValidator<TModel>[] _validators;
    private readonly bool _validateDataAnnotations;
    private readonly TimeSpan _onChangeDebounce;
    private readonly object _changeGate = new();
    private readonly List<Action<TModel>> _changeListeners = [];
    private CancellationTokenSource? _watchCancellation;
    private Task? _watchTask;
    private bool _disposed;

    /// <summary>Creates options backed by the supplied state sources.</summary>
    public ConfiglueOptions(
        StateSourceSet<TFragment> sourceSet,
        StateWriteRoute writeRoute = default,
        IEnumerable<IStateSchemaMigration<TFragment>>? migrations = null,
        IEnumerable<IConfiglueValidator<TModel>>? validators = null,
        bool validateDataAnnotations = false,
        TimeSpan? onChangeDebounce = null)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        _sourceSet = sourceSet;
        _writeRoute = writeRoute;
        _validators = validators?.ToArray() ?? [];
        _validateDataAnnotations = validateDataAnnotations;
        _onChangeDebounce = onChangeDebounce ?? TimeSpan.FromMilliseconds(300);
        if (_onChangeDebounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(onChangeDebounce), "Change debounce cannot be negative.");
        }

        if (_validators.Any(static validator => validator is null))
        {
            throw new ArgumentException("Validators cannot contain null values.", nameof(validators));
        }

        _migrationChain = new StateSchemaMigrationChain<TFragment>(TModel.ConfiglueSchema.ToMetadata(), migrations);
    }

    /// <inheritdoc />
    public IDisposable OnChange(Action<TModel> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_changeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _changeListeners.Add(listener);
            if (_watchTask is null || _watchTask.IsCompleted)
            {
                _watchCancellation?.Dispose();
                _watchCancellation = new CancellationTokenSource();
                _watchTask = WatchChangesAsync(_watchCancellation.Token);
            }
        }

        return new ChangeSubscription(this, listener);
    }

    /// <inheritdoc />
    public ValueTask<StateReadResult<TModel>> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadCoreAsync(null, null, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ConfiglueValueExplanation> ExplainAsync(
        string propertyPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        var path = propertyPath.Split('.', StringSplitOptions.None);
        if (path.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A property path cannot contain empty member names.", nameof(propertyPath));
        }

        var resolved = await ResolveCoreAsync(null, null, cancellationToken).ConfigureAwait(false);
        if (resolved.Result.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException($"Configuration state could not be read: {resolved.Result.Status}.");
        }

        var effectiveValue = GetModelValue(
            TModel.ConfiglueSchema,
            resolved.Result.Value!,
            path,
            propertyPath);
        var sourceContributions = new List<ConfiglueSourceContribution>();
        foreach (var contribution in resolved.Contributions)
        {
            if (TryGetFragmentValue(contribution.Result.Value!, path, out var value))
            {
                sourceContributions.Add(new ConfiglueSourceContribution(
                    contribution.Source.Id,
                    contribution.Source.PhysicalOrigin,
                    contribution.Result.Revision,
                    value));
            }
        }

        return new ConfiglueValueExplanation(propertyPath, effectiveValue, sourceContributions);
    }

    private async ValueTask<StateReadResult<TModel>> ReadCoreAsync(
        StateSource<TFragment>? replacementSource,
        StateReadResult<TFragment>? replacementResult,
        CancellationToken cancellationToken) =>
        (await ResolveCoreAsync(replacementSource, replacementResult, cancellationToken).ConfigureAwait(false)).Result;

    private async ValueTask<ResolvedState> ResolveCoreAsync(
        StateSource<TFragment>? replacementSource,
        StateReadResult<TFragment>? replacementResult,
        CancellationToken cancellationToken)
    {
        var contributions = new List<ResolvedContribution>();
        var revisions = new List<StateRevision>();
        StateReadResult<TFragment> lastFailure = default;

        foreach (var source in _sourceSet.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StateReadResult<TFragment> sourceResult;
            if (replacementSource is not null && replacementResult is { } replacement &&
                string.Equals(replacementSource.Id, source.Id, StringComparison.Ordinal))
            {
                sourceResult = replacement;
            }
            else
            {
                sourceResult = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            var result = sourceResult.FromSource(source.Id, source.PhysicalOrigin);
            revisions.Add(new StateRevision(source.Id, result.Revision));
            if (result.Status == StateReadStatus.Success)
            {
                if (result.Value is null)
                {
                    throw new InvalidOperationException($"State source '{source.Id}' returned a null configuration fragment.");
                }

                var fragment = result.Value;
                if (result.Schema is { } sourceSchema)
                {
                    fragment = await MigrateAsync(fragment, sourceSchema, cancellationToken).ConfigureAwait(false);
                }

                contributions.Add(new ResolvedContribution(source, result with { Value = fragment }));
                continue;
            }

            lastFailure = result;
            if (!CanFallBack(source.FallbackCondition, result.Status))
            {
                return new ResolvedState(new StateReadResult<TModel>(
                    result.Status,
                    default,
                    result.Revision,
                    result.SourceId,
                    result.PhysicalOrigin,
                    result.Schema,
                    new StateRevisionVector(revisions)), contributions, null);
            }
        }

        if (contributions.Count == 0 && lastFailure.Status == StateReadStatus.Unavailable)
        {
            return new ResolvedState(new StateReadResult<TModel>(
                lastFailure.Status,
                default,
                lastFailure.Revision,
                lastFailure.SourceId,
                lastFailure.PhysicalOrigin,
                lastFailure.Schema,
                new StateRevisionVector(revisions)), contributions, null);
        }

        var merged = TFragment.Empty;
        for (var index = contributions.Count - 1; index >= 0; index--)
        {
            merged = merged.Merge(contributions[index].Result.Value!);
        }

        var model = TModel.FromFragment(merged);
        var active = contributions.FirstOrDefault();
        var resolvedResult = StateReadResult<TModel>.Success(
            model,
            active?.Result.Revision,
            TModel.ConfiglueSchema.ToMetadata()) with
        {
            SourceId = active?.Source.Id,
            PhysicalOrigin = active?.Source.PhysicalOrigin,
            Revisions = new StateRevisionVector(revisions),
        };
        return new ResolvedState(resolvedResult, contributions, merged);
    }

    /// <inheritdoc />
    public async ValueTask<ConfigureSession<TModel>> BeginConfigureAsync(CancellationToken cancellationToken = default)
    {
        var resolved = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (resolved.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException($"Configuration state could not be read: {resolved.Status}.");
        }

        var source = SelectWriteSource();
        string? expectedRevision;
        if (resolved.Revisions is null || !resolved.Revisions.TryGetRevision(source.Id, out expectedRevision))
        {
            var current = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException($"Cannot safely begin editing because source '{source.Id}' is unavailable.");
            }

            expectedRevision = current.Revision;
        }

        var draft = resolved.Value!.DeepClone();
        var expectedRevisions = resolved.Revisions;
        return new ConfigureSession<TModel>(draft, async (value, token) =>
        {
            var latest = await ReadAsync(token).ConfigureAwait(false);
            if (latest.Status != StateReadStatus.Success || !HaveSameRevisions(expectedRevisions, latest.Revisions))
            {
                throw new StateConflictException("A state source changed after the configuration edit began.");
            }

            return await WriteToSourceAsync(source, value, expectedRevision, token).ConfigureAwait(false);
        });
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(TModel value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = SelectWriteSource();
        var current = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException($"Cannot safely write configuration because source '{source.Id}' is unavailable.");
        }

        return await WriteToSourceAsync(source, value, current.Revision, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(Action<TModel> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        using var session = await BeginConfigureAsync(cancellationToken).ConfigureAwait(false);
        var value = session.Value;
        update(value);
        session.Value = value;
        return await session.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(Func<TModel, Task> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        using var session = await BeginConfigureAsync(cancellationToken).ConfigureAwait(false);
        var value = session.Value;
        await update(value).ConfigureAwait(false);
        session.Value = value;
        return await session.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> ApplyPatchAsync(IConfigluePatch patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        cancellationToken.ThrowIfCancellationRequested();
        var modelSchema = TModel.ConfiglueSchema;
        if (patch.Schema.ModelType != typeof(TModel) || patch.Schema.Id != modelSchema.Id || patch.Schema.Version != modelSchema.Version)
        {
            throw new ArgumentException($"The patch schema '{patch.Schema.Id}' does not match '{modelSchema.Id}'.", nameof(patch));
        }

        var source = SelectWriteSource();
        var current = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException($"Cannot safely patch configuration because source '{source.Id}' is unavailable.");
        }

        if (patch.IsEmpty)
        {
            return new StateWriteResult(current.Revision);
        }

        var sourceFragment = current.Status == StateReadStatus.Success
            ? current.Value ?? throw new InvalidOperationException($"State source '{source.Id}' returned a null configuration fragment.")
            : TFragment.Empty;
        if (current.Schema is { } schema)
        {
            sourceFragment = await MigrateAsync(sourceFragment, schema, cancellationToken).ConfigureAwait(false);
        }

        if (patch.Apply(sourceFragment) is not TFragment patchedFragment)
        {
            throw new InvalidOperationException("The patch returned an incompatible configuration fragment.");
        }

        var proposedResult = StateReadResult<TFragment>.Success(
            patchedFragment,
            current.Revision,
            modelSchema.ToMetadata());
        var proposed = await ReadCoreAsync(source, proposedResult, cancellationToken).ConfigureAwait(false);
        if (proposed.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException($"The patched configuration could not be resolved: {proposed.Status}.");
        }

        Validate(proposed.Value!);
        return await source.Writer!.WriteAsync(
            new StateWriteRequest<TFragment>(patchedFragment, current.Revision, CheckRevision: true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        string sourceId,
        string targetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        cancellationToken.ThrowIfCancellationRequested();

        var source = FindSource(sourceId);
        var target = FindSource(targetId);
        if (target.Writer is null)
        {
            throw new InvalidOperationException($"State source '{target.Id}' does not support writes.");
        }

        var sourceResult = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (sourceResult.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException($"Source '{source.Id}' could not be migrated: {sourceResult.Status}.");
        }

        var sourceFragment = sourceResult.Value
            ?? throw new InvalidOperationException($"State source '{source.Id}' returned a null configuration fragment.");
        if (sourceResult.Schema is { } schema)
        {
            sourceFragment = await MigrateAsync(sourceFragment, schema, cancellationToken).ConfigureAwait(false);
        }

        var currentSchema = TModel.ConfiglueSchema.ToMetadata();
        if (ReferenceEquals(source, target) && (sourceResult.Schema is null || sourceResult.Schema == currentSchema))
        {
            return new StateSourceMigrationResult(source.Id, target.Id, sourceResult.Revision, sourceResult.Revision);
        }

        var targetResult = ReferenceEquals(source, target)
            ? sourceResult
            : await target.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (targetResult.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException($"Target source '{target.Id}' is unavailable.");
        }

        if (targetResult.Status == StateReadStatus.Success && targetResult.Value is null)
        {
            throw new InvalidOperationException($"State source '{target.Id}' returned a null configuration fragment.");
        }

        var write = await target.Writer.WriteAsync(
            new StateWriteRequest<TFragment>(sourceFragment, targetResult.Revision, CheckRevision: true),
            cancellationToken).ConfigureAwait(false);
        return new StateSourceMigrationResult(source.Id, target.Id, sourceResult.Revision, write.Revision);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_changeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _changeListeners.Clear();
            _watchCancellation?.Cancel();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task? watchTask;
        lock (_changeGate)
        {
            watchTask = _watchTask;
        }

        if (watchTask is not null)
        {
            await watchTask.ConfigureAwait(false);
        }

        _watchCancellation?.Dispose();
    }

    private StateSource<TFragment> SelectWriteSource()
    {
        var source = _writeRoute.SourceId is { } id
            ? _sourceSet.Sources.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))
            : _sourceSet.Sources.FirstOrDefault(static candidate => candidate.Writer is not null);

        if (source is null)
        {
            throw new InvalidOperationException(_writeRoute.SourceId is { } sourceId
                ? $"State source '{sourceId}' is not registered."
                : "No writable state source is registered.");
        }

        if (source.Writer is null)
        {
            throw new InvalidOperationException($"State source '{source.Id}' does not support writes.");
        }

        return source;
    }

    private StateSource<TFragment> FindSource(string sourceId) =>
        _sourceSet.Sources.FirstOrDefault(candidate => string.Equals(candidate.Id, sourceId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"State source '{sourceId}' is not registered.");

    private ValueTask<StateWriteResult> WriteToSourceAsync(
        StateSource<TFragment> source,
        TModel value,
        string? expectedRevision,
        CancellationToken cancellationToken)
    {
        Validate(value);
        return source.Writer!.WriteAsync(
            new StateWriteRequest<TFragment>(TModel.ToFragment(value), expectedRevision, CheckRevision: true),
            cancellationToken);
    }

    private void Validate(TModel value)
    {
        var failures = new List<string>();
        foreach (var validator in _validators)
        {
            failures.AddRange(validator.Validate(value));
        }

        if (_validateDataAnnotations)
        {
            var validationResults = new List<ValidationResult>();
            Validator.TryValidateObject(value!, new ValidationContext(value!), validationResults, validateAllProperties: true);
            failures.AddRange(validationResults.Select(result => result.ErrorMessage ?? "Configuration validation failed."));
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(TModel), failures);
        }
    }

    private async ValueTask<TFragment> MigrateAsync(
        TFragment value,
        StateSchemaMetadata sourceSchema,
        CancellationToken cancellationToken) =>
        await _migrationChain.MigrateAsync(value, sourceSchema, cancellationToken).ConfigureAwait(false);

    private async Task WatchChangesAsync(CancellationToken cancellationToken)
    {
        StateReadResult<TModel> previous = default;
        var hasPrevious = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!hasPrevious)
                {
                    previous = await ReadAsync(cancellationToken).ConfigureAwait(false);
                    hasPrevious = true;
                }

                await WaitForAnyChangeAsync(previous.Revisions, cancellationToken).ConfigureAwait(false);
                if (_onChangeDebounce > TimeSpan.Zero)
                {
                    await Task.Delay(_onChangeDebounce, cancellationToken).ConfigureAwait(false);
                }

                var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
                if (current.Status == StateReadStatus.Success && !HaveSameRevisions(previous.Revisions, current.Revisions))
                {
                    NotifyListeners(current.Value!);
                }
                else if (current.Status != StateReadStatus.Success)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }

                previous = current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError("Configlue failed while watching configuration changes: {0}", exception);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task WaitForAnyChangeAsync(StateRevisionVector? revisions, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTasks = new List<Task>();
        foreach (var source in _sourceSet.Sources)
        {
            if (source.Watcher is not null && revisions is not null &&
                revisions.TryGetRevision(source.Id, out var revision))
            {
                waitTasks.Add(source.Watcher.WaitForChangeAsync(revision, waitCancellation.Token).AsTask());
            }
        }

        if (waitTasks.Count == 0)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var completed = await Task.WhenAny(waitTasks).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            waitCancellation.Cancel();
        }

        try
        {
            await Task.WhenAll(waitTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The remaining source waits are canceled after the first source reports a change.
        }
    }

    private void NotifyListeners(TModel value)
    {
        Action<TModel>[] listeners;
        lock (_changeGate)
        {
            if (_disposed)
            {
                return;
            }

            listeners = _changeListeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(value.DeepClone());
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError("Configlue change listener failed: {0}", exception);
            }
        }
    }

    private static object? GetModelValue(
        ConfiglueModelSchema schema,
        object model,
        IReadOnlyList<string> path,
        string propertyPath)
    {
        object? current = model;
        for (var index = 0; index < path.Count; index++)
        {
            var member = schema.Members.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, path[index], StringComparison.Ordinal));
            if (string.IsNullOrEmpty(member.Name))
            {
                throw new ArgumentException($"Model '{schema.ModelType}' has no member named '{path[index]}'.", nameof(propertyPath));
            }

            object? value = null;
            if (current is not null)
            {
                var getter = member.GetValue
                    ?? throw new InvalidOperationException($"Generated getter metadata is missing for '{schema.ModelType}.{member.Name}'.");
                value = getter(current);
            }

            if (index == path.Count - 1)
            {
                return value;
            }

            if (member.NestedSchemaFactory is null)
            {
                throw new ArgumentException($"Member '{schema.ModelType}.{member.Name}' is not a generated nested model.", nameof(propertyPath));
            }

            schema = member.NestedSchemaFactory();
            current = value;
        }

        throw new ArgumentException("The property path is empty.", nameof(propertyPath));
    }

    private static bool TryGetFragmentValue(
        IConfiglueFragment fragment,
        IReadOnlyList<string> path,
        out object? value)
    {
        IConfiglueFragment current = fragment;
        for (var index = 0; index < path.Count; index++)
        {
            var found = false;
            object? currentValue = null;
            foreach (var member in current.EnumeratePresentMembers())
            {
                if (!string.Equals(member.Name, path[index], StringComparison.Ordinal))
                {
                    continue;
                }

                currentValue = member.Value;
                found = true;
                break;
            }

            if (!found)
            {
                value = null;
                return false;
            }

            if (index == path.Count - 1)
            {
                value = currentValue;
                return true;
            }

            if (currentValue is not IConfiglueFragment nested)
            {
                value = null;
                return false;
            }

            current = nested;
        }

        value = null;
        return false;
    }

    private static bool HaveSameRevisions(StateRevisionVector? left, StateRevisionVector? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Revisions.Count != right.Revisions.Count)
        {
            return false;
        }

        return left.Revisions.All(pair => right.TryGetRevision(pair.Key, out var revision) &&
            string.Equals(pair.Value, revision, StringComparison.Ordinal));
    }

    private void RemoveChangeListener(Action<TModel> listener)
    {
        lock (_changeGate)
        {
            _changeListeners.Remove(listener);
        }
    }

    private sealed record ResolvedContribution(StateSource<TFragment> Source, StateReadResult<TFragment> Result);

    private sealed record ResolvedState(
        StateReadResult<TModel> Result,
        IReadOnlyList<ResolvedContribution> Contributions,
        TFragment? MergedFragment);

    private sealed class ChangeSubscription(ConfiglueOptions<TModel, TFragment> owner, Action<TModel> listener) : IDisposable
    {
        private ConfiglueOptions<TModel, TFragment>? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RemoveChangeListener(listener);
    }

    private static bool CanFallBack(StateFallbackCondition condition, StateReadStatus status) =>
        status switch
        {
            StateReadStatus.NotFound => (condition & StateFallbackCondition.NotFound) != 0,
            StateReadStatus.Unavailable => (condition & StateFallbackCondition.Unavailable) != 0,
            _ => false,
        };
}
