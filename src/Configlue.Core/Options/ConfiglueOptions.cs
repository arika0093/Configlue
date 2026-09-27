using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

/// <summary>Resolves and saves a generated configuration model over a set of state sources.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
public sealed class ConfiglueOptions<TModel, TFragment>
    : IConfiglueOptions<TModel>,
        IConfiglueValueCloneProvider<TModel>,
        IDisposable,
        IAsyncDisposable
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private static readonly EventId SourceReadEvent = new(1000, "SourceRead");
    private static readonly EventId SourceReadFailedEvent = new(1001, "SourceReadFailed");
    private static readonly EventId SourceFallbackEvent = new(1002, "SourceFallback");
    private static readonly EventId SourceSelectedEvent = new(1003, "SourceSelected");
    private static readonly EventId PhysicalWriteEvent = new(1010, "PhysicalWrite");
    private static readonly EventId PhysicalWriteFailedEvent = new(1011, "PhysicalWriteFailed");
    private static readonly EventId MigrationEvent = new(1020, "StorageMigration");
    private static readonly EventId ConflictEvent = new(1030, "WriteConflict");
    private static readonly EventId WatchFailureEvent = new(1040, "WatcherFailure");
    private static readonly EventId ListenerFailureEvent = new(1041, "ChangeListenerFailure");
    private static readonly EventId WatchReloadEvent = new(1042, "WatcherReload");
    private static readonly EventId ReloadFailureListenerEvent = new(
        1043,
        "ReloadFailureListenerFailure"
    );

    private readonly StateSourceSet<TFragment> _sourceSet;
    private readonly object _sourceGate = new();
    private readonly HashSet<string> _retiredSourceIds = new(StringComparer.Ordinal);
    private StateSource<TFragment>[] _activeSources;
    private TaskCompletionSource _sourceTopologyChanged = NewTopologySignal();
    private readonly StateWriteRoute _writeRoute;
    private readonly StateWritePlan _defaultWritePlan;
    private readonly Func<TModel, TModel>? _cloneStrategy;
    private readonly StateSchemaMigrationChain<TFragment> _migrationChain;
    private readonly IConfiglueValidator<TModel>[] _validators;
    private readonly string _optionsName;
    private readonly bool _validateDataAnnotations;
    private readonly TimeSpan _onChangeDebounce;
    private readonly ILogger? _logger;
    private readonly object _changeGate = new();
    private readonly object _currentValueGate = new();
    private readonly List<Action<TModel>> _changeListeners = [];
    private readonly List<Action<Exception>> _reloadFailureListeners = [];
    private CurrentValueCacheEntry? _currentValueCache;
    private IDisposable? _currentValueCacheSubscription;
    private CancellationTokenSource? _watchCancellation;
    private Task? _watchTask;
    private TaskCompletionSource _operationsDrained = CompletedOperationsSignal();
    private readonly AsyncLocal<OperationFrame?> _operationFrame = new();
    private int _activeOperations;
    private bool _disposed;

    /// <summary>Creates options backed by the supplied state sources.</summary>
    public ConfiglueOptions(
        StateSourceSet<TFragment> sourceSet,
        StateWriteRoute writeRoute = default,
        IEnumerable<IStateSchemaMigration<TFragment>>? migrations = null,
        IEnumerable<IConfiglueValidator<TModel>>? validators = null,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        string? optionsName = null,
        ILogger? logger = null
    )
        : this(
            sourceSet,
            writeRoute,
            StateWritePlan.Empty,
            migrations,
            validators,
            validateDataAnnotations,
            onChangeDebounce,
            optionsName,
            logger
        ) { }

    /// <summary>Creates options backed by sources and registration-level property write routes.</summary>
    public ConfiglueOptions(
        StateSourceSet<TFragment> sourceSet,
        StateWriteRoute writeRoute,
        StateWritePlan defaultWritePlan,
        IEnumerable<IStateSchemaMigration<TFragment>>? migrations = null,
        IEnumerable<IConfiglueValidator<TModel>>? validators = null,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        string? optionsName = null,
        ILogger? logger = null
    )
        : this(
            sourceSet,
            writeRoute,
            defaultWritePlan,
            migrations,
            validators,
            validateDataAnnotations,
            onChangeDebounce,
            optionsName,
            logger,
            cloneStrategy: null
        ) { }

    /// <summary>Creates options with a custom model clone strategy.</summary>
    public ConfiglueOptions(
        StateSourceSet<TFragment> sourceSet,
        StateWriteRoute writeRoute,
        StateWritePlan defaultWritePlan,
        IEnumerable<IStateSchemaMigration<TFragment>>? migrations,
        IEnumerable<IConfiglueValidator<TModel>>? validators,
        bool validateDataAnnotations,
        TimeSpan? onChangeDebounce,
        string? optionsName,
        ILogger? logger,
        Func<TModel, TModel>? cloneStrategy
    )
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        ArgumentNullException.ThrowIfNull(defaultWritePlan);
        _sourceSet = sourceSet;
        _activeSources = sourceSet.Sources.ToArray();
        _writeRoute = writeRoute;
        _defaultWritePlan = defaultWritePlan;
        _cloneStrategy = cloneStrategy;
        _validators = validators?.ToArray() ?? [];
        _optionsName = optionsName ?? string.Empty;
        _logger = logger;
        _validateDataAnnotations = validateDataAnnotations;
        _onChangeDebounce = onChangeDebounce ?? TimeSpan.FromMilliseconds(300);
        if (_onChangeDebounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(onChangeDebounce),
                "Change debounce cannot be negative."
            );
        }

        if (_validators.Any(static validator => validator is null))
        {
            throw new ArgumentException(
                "Validators cannot contain null values.",
                nameof(validators)
            );
        }

        _migrationChain = new StateSchemaMigrationChain<TFragment>(
            TModel.ConfiglueSchema.ToMetadata(),
            migrations
        );
    }

    /// <inheritdoc />
    public ConfiglueOptionsDiagnostics GetDiagnostics()
    {
        StateSource<TFragment>[] activeSources;
        lock (_sourceGate)
        {
            activeSources = _activeSources;
        }

        var activeIds = activeSources
            .Select(static source => source.Id)
            .ToHashSet(StringComparer.Ordinal);
        var sources = _sourceSet
            .Sources.Select(source => new ConfiglueSourceDiagnostics(
                source.Id,
                source.Priority,
                source.FallbackCondition,
                canRead: true,
                canWrite: source.Writer is not null,
                canWatch: source.Watcher is not null,
                isActive: activeIds.Contains(source.Id),
                physicalOrigin: source.PhysicalOrigin,
                resourceId: source.ResourceId
            ))
            .ToArray();
        var defaultWriteSource =
            _writeRoute.SourceId
            ?? activeSources.FirstOrDefault(static source => source.Writer is not null)?.Id;
        return new ConfiglueOptionsDiagnostics(
            _optionsName,
            sources,
            defaultWriteSource,
            _writeRoute.SourceId is null && defaultWriteSource is not null,
            _defaultWritePlan.PropertyRoutes
        );
    }

    /// <inheritdoc />
    public TModel CurrentValue
    {
        get
        {
            var cache = Volatile.Read(ref _currentValueCache);
            if (cache is not null)
            {
                return CloneModel(cache.Value);
            }

            lock (_currentValueGate)
            {
                cache = Volatile.Read(ref _currentValueCache);
                if (cache is not null)
                {
                    return CloneModel(cache.Value);
                }

                _currentValueCacheSubscription ??= OnChange(UpdateCurrentValueCache);
                var value = ((IReadOnlyOptions<TModel>)this)
                    .GetValueAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                Volatile.Write(ref _currentValueCache, new CurrentValueCacheEntry(value));
                return CloneModel(value);
            }
        }
    }

    /// <inheritdoc />
    public IDisposable OnChange(Action<TModel> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_changeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _changeListeners.Add(listener);
            EnsureWatcherStarted();
        }

        return new ChangeSubscription(this, listener);
    }

    /// <inheritdoc />
    public IDisposable OnReloadFailed(Action<Exception> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_changeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _reloadFailureListeners.Add(listener);
            EnsureWatcherStarted();
        }

        return new ReloadFailureSubscription(this, listener);
    }

    /// <inheritdoc />
    public ValueTask<StateReadResult<TModel>> ReadAsync(
        CancellationToken cancellationToken = default
    ) => ReadPublicValueAsync(cancellationToken);

    private async ValueTask<StateReadResult<TModel>> ReadPublicValueAsync(
        CancellationToken cancellationToken
    )
    {
        var result = await ReadCoreAsync(null, cancellationToken).ConfigureAwait(false);
        return
            _cloneStrategy is not null
            && result.Status == StateReadStatus.Success
            && result.Value is not null
            ? result with
            {
                Value = CloneModel(result.Value),
            }
            : result;
    }

    /// <inheritdoc />
    public async ValueTask<ConfiglueValueExplanation> ExplainAsync(
        string propertyPath,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = EnterOperation();
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        var path = propertyPath.Split('.', StringSplitOptions.None);
        if (path.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A property path cannot contain empty member names.",
                nameof(propertyPath)
            );
        }

        var resolved = await ResolveCoreAsync(null, cancellationToken).ConfigureAwait(false);
        if (resolved.Result.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {resolved.Result.Status}."
            );
        }

        var effectiveValue = GetModelValue(
            TModel.ConfiglueSchema,
            resolved.Result.Value!,
            path,
            propertyPath,
            out var member
        );
        var sourceContributions = new List<ConfiglueSourceContribution>();
        foreach (var contribution in resolved.Contributions)
        {
            if (TryGetFragmentValue(contribution.Result.Value!, path, out var value))
            {
                sourceContributions.Add(
                    new ConfiglueSourceContribution(
                        contribution.Source.Id,
                        contribution.Result.PhysicalOrigin,
                        contribution.Result.Revision,
                        value
                    )
                );
            }
        }

        var collectionElements = IsCollectionType(member)
            ? ExplainCollectionElements(member, effectiveValue, sourceContributions)
            : [];
        return new ConfiglueValueExplanation(
            propertyPath,
            effectiveValue,
            sourceContributions,
            collectionElements
        );
    }

    private async ValueTask<StateReadResult<TModel>> ReadCoreAsync(
        IReadOnlyDictionary<string, StateReadResult<TFragment>>? replacements,
        CancellationToken cancellationToken
    ) => (await ResolveCoreAsync(replacements, cancellationToken).ConfigureAwait(false)).Result;

    private async ValueTask<ResolvedState> ResolveCoreAsync(
        IReadOnlyDictionary<string, StateReadResult<TFragment>>? replacements,
        CancellationToken cancellationToken
    )
    {
        using var operation = EnterOperation();
        var contributions = new List<ResolvedContribution>();
        var revisions = new List<StateRevision>();
        var nestedRevisions = new List<KeyValuePair<string, StateRevisionVector>>();
        StateReadResult<TFragment> lastFailure = default;

        foreach (var source in GetActiveSources())
        {
            cancellationToken.ThrowIfCancellationRequested();
            StateReadResult<TFragment> sourceResult;
            if (
                replacements is not null
                && replacements.TryGetValue(source.Id, out var replacement)
            )
            {
                sourceResult = replacement;
            }
            else
            {
                _logger?.LogTrace(
                    SourceReadEvent,
                    "Reading configuration source {SourceId} for {ModelType} options {OptionsName} at {PhysicalOrigin} ({ResourceId}).",
                    source.Id,
                    typeof(TModel).FullName,
                    _optionsName,
                    source.PhysicalOrigin,
                    source.ResourceId?.Value
                );
                try
                {
                    sourceResult = await source
                        .Reader.ReadAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                // Preserve the codec's original exception type so its recoverability policy can classify it.
#pragma warning disable S2139
                catch (Exception exception)
                {
                    _logger?.LogError(
                        SourceReadFailedEvent,
                        exception,
                        "Reading configuration source {SourceId} failed for {ModelType} options {OptionsName} at {PhysicalOrigin} ({ResourceId}).",
                        source.Id,
                        typeof(TModel).FullName,
                        _optionsName,
                        source.PhysicalOrigin,
                        source.ResourceId?.Value
                    );
                    throw;
                }
#pragma warning restore S2139
            }

            var result = sourceResult.FromSource(source.Id, source.PhysicalOrigin);
            _logger?.LogDebug(
                SourceReadEvent,
                "Configuration source {SourceId} returned {ReadStatus} for {ModelType} options {OptionsName} at {PhysicalOrigin} ({ResourceId}).",
                source.Id,
                result.Status,
                typeof(TModel).FullName,
                _optionsName,
                source.PhysicalOrigin,
                source.ResourceId?.Value
            );
            revisions.Add(new StateRevision(source.Id, result.Revision));
            if (sourceResult.Revisions is { } nestedVector)
            {
                nestedRevisions.Add(
                    new KeyValuePair<string, StateRevisionVector>(source.Id, nestedVector)
                );
            }

            if (result.Status == StateReadStatus.Success)
            {
                if (result.Value is null)
                {
                    throw new InvalidOperationException(
                        $"State source '{source.Id}' returned a null configuration fragment."
                    );
                }

                var fragment = result.Value;
                if (result.Schema is { } sourceSchema)
                {
                    _logger?.LogInformation(
                        MigrationEvent,
                        "Migrating schema from source {SourceId} from {SourceModelId} version {SourceVersion} for {ModelType} options {OptionsName}.",
                        source.Id,
                        sourceSchema.ModelId,
                        sourceSchema.Version,
                        typeof(TModel).FullName,
                        _optionsName
                    );
                    fragment = await MigrateAsync(fragment, sourceSchema, cancellationToken)
                        .ConfigureAwait(false);
                }

                contributions.Add(
                    new ResolvedContribution(source, result with { Value = fragment })
                );
                continue;
            }

            lastFailure = result;
            var canFallBack = CanFallBack(source.FallbackCondition, result.Status);
            _logger?.Log(
                result.Status == StateReadStatus.Unavailable ? LogLevel.Warning : LogLevel.Debug,
                SourceFallbackEvent,
                "Configuration source {SourceId} returned {ReadStatus}; fallback {FallbackAction} for {ModelType} options {OptionsName}.",
                source.Id,
                result.Status,
                canFallBack ? "continues" : "stops",
                typeof(TModel).FullName,
                _optionsName
            );
            if (!canFallBack)
            {
                return new ResolvedState(
                    new StateReadResult<TModel>(
                        result.Status,
                        default,
                        result.Revision,
                        result.SourceId,
                        result.PhysicalOrigin,
                        result.Schema,
                        new StateRevisionVector(revisions, nestedRevisions)
                    ),
                    contributions,
                    null
                );
            }
        }

        if (contributions.Count == 0 && lastFailure.Status == StateReadStatus.Unavailable)
        {
            return new ResolvedState(
                new StateReadResult<TModel>(
                    lastFailure.Status,
                    default,
                    lastFailure.Revision,
                    lastFailure.SourceId,
                    lastFailure.PhysicalOrigin,
                    lastFailure.Schema,
                    new StateRevisionVector(revisions, nestedRevisions)
                ),
                contributions,
                null
            );
        }

        var merged = contributions.Count == 0 ? TFragment.Empty : contributions[^1].Result.Value!;
        for (var index = contributions.Count - 2; index >= 0; index--)
        {
            merged = merged.Merge(contributions[index].Result.Value!);
        }

        var model = TModel.FromFragment(merged);
        var active = contributions.FirstOrDefault();
        var resolvedResult = StateReadResult<TModel>.Success(
            model,
            active?.Result.Revision,
            TModel.ConfiglueSchema.ToMetadata()
        ) with
        {
            SourceId = active?.Source.Id,
            PhysicalOrigin = active?.Result.PhysicalOrigin,
            Revisions = new StateRevisionVector(revisions, nestedRevisions),
        };
        if (active is not null)
        {
            _logger?.LogDebug(
                SourceSelectedEvent,
                "Configuration source {SourceId} is the highest-priority contributor for {ModelType} options {OptionsName}.",
                active.Source.Id,
                typeof(TModel).FullName,
                _optionsName
            );
        }

        return new ResolvedState(resolvedResult, contributions, merged);
    }

    /// <inheritdoc />
    public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
        CancellationToken cancellationToken = default
    ) => OpenEditSessionCoreAsync(null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        return OpenEditSessionCoreAsync(writePlan, cancellationToken);
    }

    private async ValueTask<EditSession<TModel>> OpenEditSessionCoreAsync(
        StateWritePlan? writePlan,
        CancellationToken cancellationToken
    )
    {
        using var operation = EnterOperation();
        var resolvedState = await ResolveCoreAsync(null, cancellationToken).ConfigureAwait(false);
        var resolved = resolvedState.Result;
        if (resolved.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {resolved.Status}."
            );
        }

        var source = SelectWriteSource();
        var effectiveWritePlan = _defaultWritePlan.OverrideWith(writePlan ?? StateWritePlan.Empty);
        if (effectiveWritePlan.PropertyRoutes.Count > 0)
        {
            ValidateWritePlan(effectiveWritePlan);
        }

        string? expectedRevision;
        if (
            resolved.Revisions is null
            || !resolved.Revisions.TryGetRevision(source.Id, out expectedRevision)
        )
        {
            var current = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Cannot safely begin editing because source '{source.Id}' is unavailable."
                );
            }

            expectedRevision = current.Revision;
        }

        var draft = CloneModel(resolved.Value!);
        var baseline = CloneModel(resolved.Value!);
        var defaultValue = CloneModel(TModel.FromFragment(TFragment.Empty));
        var expectedRevisions = resolved.Revisions;
        return new EditSession<TModel>(
            draft,
            async (value, token) =>
            {
                var latestState = await ResolveCoreAsync(null, token).ConfigureAwait(false);
                var latest = latestState.Result;
                if (latest.Status != StateReadStatus.Success)
                {
                    throw new InvalidOperationException(
                        $"Configuration state could not be read before saving: {latest.Status}."
                    );
                }

                var hasRevisionChanges = !HaveSameRevisions(expectedRevisions, latest.Revisions);
                var saveBaseline = baseline;
                var saveValue = value;
                var saveContributions = latestState.Contributions;
                var saveRevisions = latest.Revisions;
                string? saveExpectedRevision = null;
                if (
                    saveRevisions is null
                    || !saveRevisions.TryGetRevision(source.Id, out saveExpectedRevision)
                )
                {
                    var current = await source.Reader.ReadAsync(token).ConfigureAwait(false);
                    if (current.Status == StateReadStatus.Unavailable)
                    {
                        throw new InvalidOperationException(
                            $"Cannot safely begin editing because source '{source.Id}' is unavailable."
                        );
                    }

                    saveExpectedRevision = current.Revision;
                }

                if (hasRevisionChanges)
                {
                    saveBaseline = latest.Value!;
                    saveValue = RebaseConfigurationEdit(baseline, value, saveBaseline);
                }

                StateWriteResult writeResult;
                if (effectiveWritePlan.PropertyRoutes.Count == 0)
                {
                    writeResult = await WriteChangesToSourceAsync(
                            source,
                            saveBaseline,
                            saveValue,
                            saveExpectedRevision,
                            saveContributions,
                            saveRevisions,
                            token
                        )
                        .ConfigureAwait(false);
                }
                else
                {
                    writeResult = await WriteChangesToSourcesAsync(
                            source,
                            saveBaseline,
                            saveValue,
                            saveExpectedRevision,
                            saveRevisions,
                            saveContributions,
                            effectiveWritePlan,
                            token
                        )
                        .ConfigureAwait(false);
                }

                baseline = value;
                expectedRevisions = latest.Revisions;
                expectedRevision = saveExpectedRevision;
                return writeResult;
            },
            CloneModel,
            baseline,
            defaultValue
        );
    }

    private TModel RebaseConfigurationEdit(TModel before, TModel desired, TModel current)
    {
        var changes = TModel.Diff(before, desired);
        if (changes.IsEmpty)
        {
            return current;
        }

        var rebased = RebaseChanges(TModel.ConfiglueSchema, changes, before, desired, current, []);
        var currentFragment = TModel.ToFragment(current);
        if (currentFragment.ApplyChanges((TFragment)rebased) is not TFragment updated)
        {
            throw new InvalidOperationException(
                "The rebased edit produced an incompatible fragment."
            );
        }

        var result = TModel.FromFragment(updated);
        return result;
    }

    private IConfiglueFragment RebaseChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        object before,
        object desired,
        object current,
        List<string> path
    )
    {
        foreach (var change in changes.EnumeratePresentMembers().ToArray())
        {
            var member = schema.Members.FirstOrDefault(candidate => candidate.Id == change.Id);
            if (string.IsNullOrEmpty(member.Name))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.Id}' has no member with id {change.Id}."
                );
            }

            path.Add(member.Name);
            try
            {
                var beforeValue = member.GetValue?.Invoke(before);
                var desiredValue = member.GetValue?.Invoke(desired);
                var currentValue = member.GetValue?.Invoke(current);
                if (
                    member.NestedSchemaFactory is not null
                    && change.Value is IConfiglueFragment nestedChanges
                    && beforeValue is not null
                    && desiredValue is not null
                    && currentValue is not null
                )
                {
                    var nested = RebaseChanges(
                        member.NestedSchemaFactory(),
                        nestedChanges,
                        beforeValue,
                        desiredValue,
                        currentValue,
                        path
                    );
                    changes = changes.WithMember(member.Id, nested);
                    continue;
                }

                if (member.MergeStrategy is { } mergeStrategy)
                {
                    if (
                        !mergeStrategy.TryRebase(
                            beforeValue,
                            desiredValue,
                            currentValue,
                            out var rebasedValue,
                            out var reason
                        )
                    )
                    {
                        throw LogConflict(
                            reason
                                ?? $"The custom merge strategy could not rebase the edit to '{string.Join('.', path)}'."
                        );
                    }

                    changes = changes.WithMember(member.Id, rebasedValue);
                    continue;
                }

                if (member.CollectionValueFactory is not null)
                {
                    var rebasedCollection = RebaseCollectionEdit(
                        member,
                        beforeValue,
                        desiredValue,
                        currentValue,
                        string.Join('.', path)
                    );
                    if (rebasedCollection is not null)
                    {
                        changes = changes.WithMember(member.Id, rebasedCollection);
                    }

                    continue;
                }

                if (
                    !AreEditValuesEqual(member, beforeValue, currentValue)
                    && !AreEditValuesEqual(member, desiredValue, currentValue)
                )
                {
                    throw LogConflict(
                        $"The configuration edit conflicts with a concurrent change to '{string.Join('.', path)}'."
                    );
                }
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        return changes;
    }

    private object? RebaseCollectionEdit(
        ConfiglueMemberSchema member,
        object? beforeValue,
        object? desiredValue,
        object? currentValue,
        string propertyPath
    )
    {
        if (
            beforeValue is not System.Collections.IEnumerable beforeEnumerable
            || beforeValue is string
            || desiredValue is not System.Collections.IEnumerable desiredEnumerable
            || desiredValue is string
            || currentValue is not System.Collections.IEnumerable currentEnumerable
            || currentValue is string
        )
        {
            if (
                !AreEditValuesEqual(member, beforeValue, currentValue)
                && !AreEditValuesEqual(member, desiredValue, currentValue)
            )
            {
                throw LogConflict(
                    $"The configuration edit conflicts with a concurrent change to '{propertyPath}'."
                );
            }

            return null;
        }

        var before = beforeEnumerable.Cast<object?>().ToList();
        var desired = desiredEnumerable.Cast<object?>().ToList();
        var current = currentEnumerable.Cast<object?>().ToList();
        if (member.MergeMode == MergeMode.Append)
        {
            if (desired.Count < before.Count || !desired.Take(before.Count).SequenceEqual(before))
            {
                if (
                    !AreEditValuesEqual(member, current, before)
                    && !AreEditValuesEqual(member, current, desired)
                )
                {
                    throw LogConflict(
                        $"The configuration edit conflicts with a concurrent change to append-merged member '{propertyPath}'."
                    );
                }

                return member.CollectionValueFactory!(desired);
            }

            if (current.Count < before.Count || !current.Take(before.Count).SequenceEqual(before))
            {
                throw LogConflict(
                    $"The configuration edit cannot reapply its append to '{propertyPath}' because the existing collection prefix changed."
                );
            }

            var additions = desired.Skip(before.Count);
            return member.CollectionValueFactory!(current.Concat(additions));
        }

        if (member.MergeMode == MergeMode.SetUnion)
        {
            var removed = before.Where(value => !desired.Contains(value)).ToArray();
            if (
                removed.Length > 0
                && !AreEditValuesEqual(member, current, before)
                && !AreEditValuesEqual(member, current, desired)
            )
            {
                throw LogConflict(
                    $"The configuration edit conflicts with a concurrent change to set-union member '{propertyPath}'."
                );
            }

            if (removed.Length > 0)
            {
                return member.CollectionValueFactory!(desired);
            }

            var rebased = current.ToList();
            foreach (
                var value in desired
                    .Where(value => !before.Contains(value))
                    .Where(value => !rebased.Contains(value))
            )
            {
                rebased.Add(value);
            }

            return member.CollectionValueFactory!(rebased);
        }

        if (
            !AreEditValuesEqual(member, beforeValue, currentValue)
            && !AreEditValuesEqual(member, desiredValue, currentValue)
        )
        {
            throw LogConflict(
                $"The configuration edit conflicts with a concurrent change to '{propertyPath}'."
            );
        }

        return null;
    }

    private static bool AreEditValuesEqual(
        ConfiglueMemberSchema member,
        object? left,
        object? right
    )
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (
            left is System.Collections.IEnumerable leftValues
            && left is not string
            && right is System.Collections.IEnumerable rightValues
            && right is not string
        )
        {
            if (IsSetCollectionType(member.ValueType))
            {
                return new HashSet<object?>(leftValues.Cast<object?>()).SetEquals(
                    rightValues.Cast<object?>()
                );
            }

            return leftValues.Cast<object?>().SequenceEqual(rightValues.Cast<object?>());
        }

        return Equals(left, right);
    }

    private static bool IsSetCollectionType(Type valueType)
    {
        if (!valueType.IsGenericType)
        {
            return false;
        }

        var definition = valueType.GetGenericTypeDefinition();
        return definition == typeof(HashSet<>)
            || definition == typeof(ISet<>)
            || definition == typeof(IReadOnlySet<>);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(
        IConfigluePatch patch,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(patch);
        cancellationToken.ThrowIfCancellationRequested();
        var modelSchema = TModel.ConfiglueSchema;
        if (
            patch.Schema.ModelType != typeof(TModel)
            || patch.Schema.Id != modelSchema.Id
            || patch.Schema.Version != modelSchema.Version
        )
        {
            throw new ArgumentException(
                $"The patch schema '{patch.Schema.Id}' does not match '{modelSchema.Id}'.",
                nameof(patch)
            );
        }

        var source = SelectWriteSource();
        if (patch.IsEmpty)
        {
            var current = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Cannot safely patch configuration because source '{source.Id}' is unavailable."
                );
            }

            return new StateWriteResult(current.Revision);
        }

        if (_defaultWritePlan.PropertyRoutes.Count > 0)
        {
            ValidateWritePlan(_defaultWritePlan);
        }

        IReadOnlyDictionary<string, IConfigluePatch> patchesBySource;
        if (patch is IConfiglueRoutablePatch routablePatch)
        {
            patchesBySource = routablePatch.Route(_defaultWritePlan, source.Id);
        }
        else if (patch is IConfiglueMemberPatch memberPatch)
        {
            var routed = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var member in modelSchema.Members)
            {
                var selected = memberPatch.SelectMembers([member.Id]);
                if (selected.IsEmpty)
                {
                    continue;
                }

                var targetSourceId = _defaultWritePlan.ResolveSourceId(member.Name, source.Id);
                if (!routed.TryGetValue(targetSourceId, out var memberIds))
                {
                    memberIds = [];
                    routed.Add(targetSourceId, memberIds);
                }

                memberIds.Add(member.Id);
            }

            patchesBySource =
                routed.Count == 0
                    ? new Dictionary<string, IConfigluePatch>(StringComparer.Ordinal)
                    {
                        [source.Id] = patch,
                    }
                    : routed.ToDictionary(
                        static route => route.Key,
                        route => memberPatch.SelectMembers(route.Value.ToArray()),
                        StringComparer.Ordinal
                    );
        }
        else
        {
            patchesBySource = new Dictionary<string, IConfigluePatch>(StringComparer.Ordinal)
            {
                [source.Id] = patch,
            };
        }

        var sourcePatches = patchesBySource
            .Select(static route => new StateSourcePatch(route.Key, route.Value))
            .ToArray();
        var result = await ApplyPatchesCoreAsync(sourcePatches, null, null, cancellationToken)
            .ConfigureAwait(false);
        var sourceResult = result.Sources.FirstOrDefault(route =>
            string.Equals(route.SourceId, source.Id, StringComparison.Ordinal)
        );
        var revision = sourceResult.SourceId is null
            ? result.Sources[0].Revision
            : sourceResult.Revision;
        return result.Sources.Count == 1
            ? new StateWriteResult(revision)
            : new StateWriteResult(revision) { MultiWriteResult = result };
    }

    /// <inheritdoc />
    public ValueTask<StateMultiWriteResult> ApplyPatchesAsync(
        IEnumerable<StateSourcePatch> patches,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(patches);
        cancellationToken.ThrowIfCancellationRequested();
        return ApplyPatchesCoreAsync(patches.ToArray(), null, null, cancellationToken);
    }

    private async ValueTask<StateMultiWriteResult> ApplyPatchesCoreAsync(
        StateSourcePatch[] patchRequests,
        StateRevisionVector? expectedBaselineRevisions,
        object? expectedResolvedModel,
        CancellationToken cancellationToken
    )
    {
        using var operation = EnterOperation();
        cancellationToken.ThrowIfCancellationRequested();
        if (patchRequests.Length == 0)
        {
            throw new ArgumentException(
                "At least one source patch is required.",
                nameof(patchRequests)
            );
        }

        if (patchRequests.Any(static patch => patch is null))
        {
            throw new ArgumentException(
                "A patch batch cannot contain null entries.",
                nameof(patchRequests)
            );
        }

        if (
            patchRequests
                .Select(static patch => patch.SourceId)
                .Distinct(StringComparer.Ordinal)
                .Count() != patchRequests.Length
        )
        {
            throw new ArgumentException(
                "A source can only appear once in a patch batch.",
                nameof(patchRequests)
            );
        }

        var modelSchema = TModel.ConfiglueSchema;
        foreach (var schema in patchRequests.Select(static request => request.Patch.Schema))
        {
            if (
                schema.ModelType != typeof(TModel)
                || schema.Id != modelSchema.Id
                || schema.Version != modelSchema.Version
            )
            {
                throw new ArgumentException(
                    $"The patch schema '{schema.Id}' does not match '{modelSchema.Id}'.",
                    nameof(patchRequests)
                );
            }
        }

        var baseline = await ResolveCoreAsync(null, cancellationToken).ConfigureAwait(false);
        if (baseline.Result.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {baseline.Result.Status}."
            );
        }

        if (
            expectedBaselineRevisions is not null
            && !HaveSameRevisions(expectedBaselineRevisions, baseline.Result.Revisions)
        )
        {
            throw LogConflict("A state source changed after the configuration edit began.");
        }

        var replacements = new Dictionary<string, StateReadResult<TFragment>>(
            StringComparer.Ordinal
        );
        var noOpResults = new Dictionary<string, StateSourceWriteResult>(StringComparer.Ordinal);
        var writePlans =
            new List<(
                StateSource<TFragment> Source,
                IStateWriter<TFragment> Writer,
                StateWriteRequest<TFragment> Request,
                ResourceId? ResourceId,
                IResourceBatchWriter? BatchWriter,
                ResourceWriteMutation? Mutation
            )>();

        foreach (var patchRequest in patchRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = FindSource(patchRequest.SourceId);
            if (!IsSourceActive(source.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' has been retired from this options instance."
                );
            }

            var current = (
                await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ).FromSource(source.Id, source.PhysicalOrigin);
            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Cannot safely patch configuration because source '{source.Id}' is unavailable."
                );
            }

            if (
                baseline.Result.Revisions is null
                || !baseline.Result.Revisions.TryGetRevision(source.Id, out var baselineRevision)
                || !string.Equals(current.Revision, baselineRevision, StringComparison.Ordinal)
            )
            {
                throw LogConflict(
                    $"State source '{source.Id}' changed while the patch batch was being prepared."
                );
            }

            if (patchRequest.Patch.IsEmpty)
            {
                noOpResults.Add(
                    source.Id,
                    new StateSourceWriteResult(source.Id, source.ResourceId, current.Revision)
                );
                continue;
            }

            if (source.Reader is CompositeStateSource<TFragment> composite)
            {
                if (patchRequest.Patch is not IConfiglueMemberPatch memberPatch)
                {
                    throw new NotSupportedException(
                        $"Patch for composite source '{source.Id}' must support member selection."
                    );
                }

                var invalidMemberRoute = composite.WritePlan.PropertyRoutes.Keys.FirstOrDefault(
                    memberRoute => !IsValidMemberPath(modelSchema, memberRoute.Split('.'))
                );
                if (invalidMemberRoute is not null)
                {
                    throw new InvalidOperationException(
                        $"Composite write route '{invalidMemberRoute}' does not match a model member path."
                    );
                }

                var routedPatches = new Dictionary<string, IConfigluePatch>(StringComparer.Ordinal);
                if (patchRequest.Patch is FragmentChangesPatch fragmentPatch)
                {
                    var routedChanges = PartitionCompositeChanges(
                        modelSchema,
                        fragmentPatch.Changes,
                        composite,
                        []
                    );
                    foreach (var (componentId, componentChanges) in routedChanges)
                    {
                        routedPatches.Add(
                            componentId,
                            new FragmentChangesPatch((TFragment)componentChanges)
                        );
                    }
                }
                else if (patchRequest.Patch is IConfiglueRoutablePatch routablePatch)
                {
                    foreach (
                        var (componentId, componentPatch) in routablePatch.Route(
                            composite.WritePlan,
                            composite.DefaultWriteSourceId
                        )
                    )
                    {
                        routedPatches.Add(componentId, componentPatch);
                    }
                }
                else
                {
                    var routedMemberIds = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                    foreach (var member in modelSchema.Members)
                    {
                        var selected = memberPatch.SelectMembers([member.Id]);
                        if (selected.IsEmpty)
                        {
                            continue;
                        }

                        if (composite.HasWriteRouteBelow(member.Name))
                        {
                            throw new NotSupportedException(
                                $"Patch for nested member '{member.Name}' must use a model edit so its nested changes can be routed."
                            );
                        }

                        var component = composite.ResolveWriteComponent(member.Name);
                        if (!routedMemberIds.TryGetValue(component.Id, out var memberIds))
                        {
                            memberIds = [];
                            routedMemberIds.Add(component.Id, memberIds);
                        }

                        memberIds.Add(member.Id);
                    }

                    foreach (var (componentId, memberIds) in routedMemberIds)
                    {
                        routedPatches.Add(
                            componentId,
                            memberPatch.SelectMembers(memberIds.ToArray())
                        );
                    }
                }

                if (routedPatches.Count == 0)
                {
                    noOpResults.Add(
                        source.Id,
                        new StateSourceWriteResult(source.Id, source.ResourceId, current.Revision)
                    );
                    continue;
                }

                if (
                    baseline.Result.Revisions is null
                    || !baseline.Result.Revisions.TryGetNestedRevisions(
                        source.Id,
                        out var nestedBaseline
                    )
                    || nestedBaseline is null
                )
                {
                    throw LogConflict(
                        $"Composite source '{source.Id}' has no component revision baseline."
                    );
                }

                var componentOverrides = new Dictionary<string, TFragment>(StringComparer.Ordinal);
                foreach (var (componentId, componentPatch) in routedPatches)
                {
                    var component = composite.Components.First(item =>
                        string.Equals(item.Id, componentId, StringComparison.Ordinal)
                    );
                    var componentCurrent = (
                        await component.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ).FromSource(component.Id, component.PhysicalOrigin);
                    if (componentCurrent.Status == StateReadStatus.Unavailable)
                    {
                        throw new InvalidOperationException(
                            $"Cannot safely patch configuration because component '{component.Id}' is unavailable."
                        );
                    }

                    var currentComponentRevisions =
                        componentCurrent.Revisions
                        ?? new StateRevisionVector([
                            new StateRevision(component.Id, componentCurrent.Revision),
                        ]);
                    if (!HaveSameRevisions(nestedBaseline, currentComponentRevisions))
                    {
                        throw LogConflict(
                            $"Component source '{component.Id}' changed while the patch batch was being prepared."
                        );
                    }

                    var componentFragment = componentCurrent.Status switch
                    {
                        StateReadStatus.NotFound => TFragment.Empty,
                        StateReadStatus.Success => componentCurrent.Value
                            ?? throw new InvalidOperationException(
                                $"Component source '{component.Id}' returned a null fragment."
                            ),
                        _ => throw new InvalidOperationException(
                            $"Component source '{component.Id}' could not be patched: {componentCurrent.Status}."
                        ),
                    };
                    if (componentCurrent.Schema is { } componentSchema)
                    {
                        componentFragment = await MigrateAsync(
                                componentFragment,
                                componentSchema,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }

                    if (componentPatch.Apply(componentFragment) is not TFragment patchedComponent)
                    {
                        throw new InvalidOperationException(
                            $"The patch for component '{component.Id}' returned an incompatible fragment."
                        );
                    }

                    componentOverrides.Add(component.Id, patchedComponent);
                    var componentRequest = new StateWriteRequest<TFragment>(
                        patchedComponent,
                        componentCurrent.Revision,
                        CheckRevision: true
                    );
                    var componentResourceId = component.ResourceId;
                    IResourceBatchWriter? componentBatchWriter = null;
                    ResourceWriteMutation? componentMutation = null;
                    ResourceId? componentParticipantResourceId = null;
                    if (
                        component.Writer is IAsyncStateWriteBatchParticipant<TFragment>
                        {
                            CanPrepareBatchWrite: true,
                        } componentAsyncParticipant
                    )
                    {
                        var batchPlan = await componentAsyncParticipant
                            .TryCreateBatchWriteAsync(componentRequest, cancellationToken)
                            .ConfigureAwait(false);
                        if (batchPlan is { } prepared)
                        {
                            componentParticipantResourceId = prepared.ResourceId;
                            componentBatchWriter = prepared.BatchWriter;
                            componentMutation = prepared.Mutation;
                        }
                    }
                    else if (
                        component.Writer is IStateWriteBatchParticipant<TFragment> participant
                        && participant.TryCreateBatchWrite(
                            componentRequest,
                            out var synchronousResourceId,
                            out componentBatchWriter,
                            out componentMutation
                        )
                    )
                    {
                        componentParticipantResourceId = synchronousResourceId;
                    }

                    if (componentParticipantResourceId is { } componentResolvedResourceId)
                    {
                        if (
                            componentResourceId is { } declaredResourceId
                            && declaredResourceId != componentResolvedResourceId
                        )
                        {
                            throw new InvalidOperationException(
                                $"State source '{component.Id}' declares resource '{declaredResourceId}' but its writer targets '{componentResolvedResourceId}'."
                            );
                        }

                        if (
                            componentBatchWriter is IResourceIdentity batchIdentity
                            && batchIdentity.ResourceId != componentResolvedResourceId
                        )
                        {
                            throw new InvalidOperationException(
                                $"State source '{component.Id}' prepares a mutation for '{componentResolvedResourceId}' but its batch writer targets '{batchIdentity.ResourceId}'."
                            );
                        }

                        componentResourceId = componentResolvedResourceId;
                    }

                    writePlans.Add(
                        (
                            component,
                            component.Writer!,
                            componentRequest,
                            componentResourceId,
                            componentBatchWriter,
                            componentMutation
                        )
                    );
                }

                foreach (var component in composite.Components)
                {
                    if (componentOverrides.ContainsKey(component.Id))
                    {
                        continue;
                    }

                    var componentState = (
                        await component.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ).FromSource(component.Id, component.PhysicalOrigin);
                    if (
                        componentState.Status != StateReadStatus.Success
                        || componentState.Value is null
                    )
                    {
                        continue;
                    }

                    var componentValue = componentState.Value;
                    if (componentState.Schema is { } componentSchema)
                    {
                        componentValue = await MigrateAsync(
                                componentValue,
                                componentSchema,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }

                    componentOverrides.Add(component.Id, componentValue);
                }

                var composed = await composite
                    .ReadWithOverridesAsync(componentOverrides, cancellationToken)
                    .ConfigureAwait(false);
                if (composed.Status != StateReadStatus.Success || composed.Value is null)
                {
                    throw new InvalidOperationException(
                        $"Patched composite source '{source.Id}' could not be resolved: {composed.Status}."
                    );
                }

                replacements.Add(
                    source.Id,
                    StateReadResult<TFragment>.Success(
                        composed.Value,
                        current.Revision,
                        modelSchema.ToMetadata()
                    ) with
                    {
                        Revisions = composed.Revisions,
                    }
                );
                continue;
            }

            var sourceFragment = current.Status switch
            {
                StateReadStatus.NotFound => TFragment.Empty,
                StateReadStatus.Success => current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{source.Id}' returned a null configuration fragment."
                    ),
                _ => throw new InvalidOperationException(
                    $"Source '{source.Id}' could not be patched: {current.Status}."
                ),
            };
            if (current.Schema is { } schema)
            {
                sourceFragment = await MigrateAsync(sourceFragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (patchRequest.Patch.Apply(sourceFragment) is not TFragment patchedFragment)
            {
                throw new InvalidOperationException(
                    $"The patch for source '{source.Id}' returned an incompatible fragment."
                );
            }

            if (source.Writer is null)
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' does not support writes."
                );
            }

            var request = new StateWriteRequest<TFragment>(
                patchedFragment,
                current.Revision,
                CheckRevision: true
            );
            var sourceResourceId = source.ResourceId;
            IResourceBatchWriter? batchWriter = null;
            ResourceWriteMutation? mutation = null;
            ResourceId? participantResourceId = null;
            if (
                source.Writer is IAsyncStateWriteBatchParticipant<TFragment>
                {
                    CanPrepareBatchWrite: true,
                } asyncParticipant
            )
            {
                var batchPlan = await asyncParticipant
                    .TryCreateBatchWriteAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (batchPlan is { } prepared)
                {
                    participantResourceId = prepared.ResourceId;
                    batchWriter = prepared.BatchWriter;
                    mutation = prepared.Mutation;
                }
            }
            else if (
                source.Writer is IStateWriteBatchParticipant<TFragment> participant
                && participant.TryCreateBatchWrite(
                    request,
                    out var synchronousResourceId,
                    out batchWriter,
                    out mutation
                )
            )
            {
                participantResourceId = synchronousResourceId;
            }

            if (participantResourceId is { } resolvedResourceId)
            {
                if (
                    sourceResourceId is { } declaredResourceId
                    && declaredResourceId != resolvedResourceId
                )
                {
                    throw new InvalidOperationException(
                        $"State source '{source.Id}' declares resource '{declaredResourceId}' but its writer targets '{resolvedResourceId}'."
                    );
                }

                if (
                    batchWriter is IResourceIdentity batchIdentity
                    && batchIdentity.ResourceId != resolvedResourceId
                )
                {
                    throw new InvalidOperationException(
                        $"State source '{source.Id}' prepares a mutation for '{resolvedResourceId}' but its batch writer targets '{batchIdentity.ResourceId}'."
                    );
                }

                sourceResourceId = resolvedResourceId;
            }

            var proposed = StateReadResult<TFragment>.Success(
                patchedFragment,
                current.Revision,
                modelSchema.ToMetadata()
            );
            replacements.Add(source.Id, proposed);
            writePlans.Add(
                (source, source.Writer, request, sourceResourceId, batchWriter, mutation)
            );
        }

        if (replacements.Count > 0)
        {
            var proposed = await ResolveCoreAsync(replacements, cancellationToken)
                .ConfigureAwait(false);
            if (proposed.Result.Status != StateReadStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Patched configuration could not be resolved: {proposed.Result.Status}."
                );
            }

            if (
                expectedResolvedModel is TModel expectedModel
                && TModel.Diff(proposed.Result.Value!, expectedModel) is { IsEmpty: false } mismatch
            )
            {
                var paths = GetChangedPropertyPaths(TModel.ConfiglueSchema, mismatch, []);
                var readonlySources = proposed
                    .Contributions.Where(static contribution => contribution.Source.Writer is null)
                    .Select(static contribution => contribution.Source.Id)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var details = paths.Count == 0 ? "the requested values" : string.Join(", ", paths);
                var shadowing =
                    readonlySources.Length == 0
                        ? string.Empty
                        : $" Read-only source(s) contributing to the resolved state: '{string.Join("', '", readonlySources)}'.";
                throw LogConflict(
                    $"The configured source routes cannot realize the requested edit for '{details}'. A higher-priority contribution may shadow the write.{shadowing}"
                );
            }

            if (!HaveSameRevisions(baseline.Result.Revisions, proposed.Result.Revisions))
            {
                throw LogConflict(
                    "A state source changed while the patch batch was being resolved."
                );
            }

            Validate(proposed.Result.Value!);
        }

        var resourceGroups =
            new Dictionary<
                ResourceId,
                List<(
                    StateSource<TFragment> Source,
                    IStateWriter<TFragment> Writer,
                    StateWriteRequest<TFragment> Request,
                    ResourceId? ResourceId,
                    IResourceBatchWriter? BatchWriter,
                    ResourceWriteMutation? Mutation
                )>
            >();
        var independentWrites =
            new List<
                List<(
                    StateSource<TFragment> Source,
                    IStateWriter<TFragment> Writer,
                    StateWriteRequest<TFragment> Request,
                    ResourceId? ResourceId,
                    IResourceBatchWriter? BatchWriter,
                    ResourceWriteMutation? Mutation
                )>
            >();
        foreach (var plan in writePlans)
        {
            if (plan.ResourceId is not { } resourceId)
            {
                independentWrites.Add([plan]);
                continue;
            }

            if (!resourceGroups.TryGetValue(resourceId, out var group))
            {
                group = [];
                resourceGroups.Add(resourceId, group);
            }

            group.Add(plan);
        }

        var writeGroups = resourceGroups.Values.Concat(independentWrites).ToArray();
        foreach (var group in writeGroups.Where(static group => group.Count > 1))
        {
            if (group.Any(static plan => plan.BatchWriter is null || plan.Mutation is null))
            {
                var sourceIds = string.Join("', '", group.Select(static plan => plan.Source.Id));
                throw new NotSupportedException(
                    $"Sources '{sourceIds}' share one ResourceId but their writers cannot batch physical mutations."
                );
            }

            ResourceWriteMutation.ValidateBatch(
                group.Select(static plan => plan.Mutation!).ToArray()
            );
        }

        if (writePlans.Count > 0)
        {
            var latest = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (
                latest.Status != StateReadStatus.Success
                || !HaveSameRevisions(baseline.Result.Revisions, latest.Revisions)
            )
            {
                throw LogConflict(
                    "A state source changed before the patch batch could be written."
                );
            }
        }

        var results = new Dictionary<string, StateSourceWriteResult>(
            noOpResults,
            StringComparer.Ordinal
        );
        var physicalWriteCount = 0;
        for (var groupIndex = 0; groupIndex < writeGroups.Length; groupIndex++)
        {
            var group = writeGroups[groupIndex];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException exception) when (physicalWriteCount > 0)
            {
                throw CreatePartialWriteException(
                    exception,
                    group,
                    writeGroups.Skip(groupIndex + 1),
                    results.Values,
                    physicalWriteCount
                );
            }

            var sourceIds = string.Join(",", group.Select(static plan => plan.Source.Id));
            var resourceId = group[0].ResourceId?.Value;
            if (group.Count == 1)
            {
                var plan = group[0];
                _logger?.LogInformation(
                    PhysicalWriteEvent,
                    "Writing configuration state for {ModelType} options {OptionsName} through source {SourceId} at resource {ResourceId}.",
                    typeof(TModel).FullName,
                    _optionsName,
                    plan.Source.Id,
                    resourceId
                );
                StateWriteResult write;
                try
                {
                    write = await plan
                        .Writer.WriteAsync(plan.Request, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                    when (cancellationToken.IsCancellationRequested)
                {
                    if (physicalWriteCount > 0)
                    {
                        throw CreatePartialWriteException(
                            exception,
                            group,
                            writeGroups.Skip(groupIndex + 1),
                            results.Values,
                            physicalWriteCount
                        );
                    }

                    throw;
                }
                // Preserve the writer's exception type for callers that classify conflicts or retries.
#pragma warning disable S2139
                catch (Exception exception)
                {
                    _logger?.LogError(
                        PhysicalWriteFailedEvent,
                        exception,
                        "Writing configuration state failed for {ModelType} options {OptionsName} through source {SourceId} at resource {ResourceId}.",
                        typeof(TModel).FullName,
                        _optionsName,
                        plan.Source.Id,
                        resourceId
                    );
                    if (physicalWriteCount > 0)
                    {
                        throw CreatePartialWriteException(
                            exception,
                            group,
                            writeGroups.Skip(groupIndex + 1),
                            results.Values,
                            physicalWriteCount
                        );
                    }

                    throw;
                }
#pragma warning restore S2139
                InvalidateCurrentValueCache();
                results.Add(
                    plan.Source.Id,
                    new StateSourceWriteResult(plan.Source.Id, plan.ResourceId, write.Revision)
                );
                _logger?.LogDebug(
                    PhysicalWriteEvent,
                    "Wrote configuration state through source {SourceId} at resource {ResourceId} for {ModelType} options {OptionsName}.",
                    plan.Source.Id,
                    resourceId,
                    typeof(TModel).FullName,
                    _optionsName
                );
                physicalWriteCount++;
                continue;
            }

            var batchWriter = group[0].BatchWriter!;
            _logger?.LogInformation(
                PhysicalWriteEvent,
                "Writing a physical batch for {ModelType} options {OptionsName} through sources {SourceIds} at resource {ResourceId}.",
                typeof(TModel).FullName,
                _optionsName,
                sourceIds,
                resourceId
            );
            StateWriteResult batchResult;
            try
            {
                batchResult = await batchWriter
                    .WriteBatchAsync(
                        group.Select(static plan => plan.Mutation!).ToArray(),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                if (physicalWriteCount > 0)
                {
                    throw CreatePartialWriteException(
                        exception,
                        group,
                        writeGroups.Skip(groupIndex + 1),
                        results.Values,
                        physicalWriteCount
                    );
                }

                throw;
            }
            // Preserve the batch writer's exception type for conflict and retry handling.
#pragma warning disable S2139
            catch (Exception exception)
            {
                _logger?.LogError(
                    PhysicalWriteFailedEvent,
                    exception,
                    "The physical batch write failed for {ModelType} options {OptionsName} through sources {SourceIds} at resource {ResourceId}.",
                    typeof(TModel).FullName,
                    _optionsName,
                    sourceIds,
                    resourceId
                );
                if (physicalWriteCount > 0)
                {
                    throw CreatePartialWriteException(
                        exception,
                        group,
                        writeGroups.Skip(groupIndex + 1),
                        results.Values,
                        physicalWriteCount
                    );
                }

                throw;
            }
#pragma warning restore S2139
            InvalidateCurrentValueCache();
            foreach (var plan in group)
            {
                results.Add(
                    plan.Source.Id,
                    new StateSourceWriteResult(
                        plan.Source.Id,
                        plan.ResourceId,
                        batchResult.Revision
                    )
                );
            }

            physicalWriteCount++;
            _logger?.LogDebug(
                PhysicalWriteEvent,
                "Wrote one physical batch for {ModelType} options {OptionsName} through sources {SourceIds} at resource {ResourceId}.",
                typeof(TModel).FullName,
                _optionsName,
                sourceIds,
                resourceId
            );
        }

        return new StateMultiWriteResult(results.Values, physicalWriteCount);

        static StateMultiWriteException CreatePartialWriteException(
            Exception exception,
            List<(
                StateSource<TFragment> Source,
                IStateWriter<TFragment> Writer,
                StateWriteRequest<TFragment> Request,
                ResourceId? ResourceId,
                IResourceBatchWriter? BatchWriter,
                ResourceWriteMutation? Mutation
            )> failedGroup,
            IEnumerable<
                List<(
                    StateSource<TFragment> Source,
                    IStateWriter<TFragment> Writer,
                    StateWriteRequest<TFragment> Request,
                    ResourceId? ResourceId,
                    IResourceBatchWriter? BatchWriter,
                    ResourceWriteMutation? Mutation
                )>
            > remainingGroups,
            IEnumerable<StateSourceWriteResult> completed,
            int completedPhysicalWrites
        )
        {
            var failedPlan = failedGroup[0];
            return new StateMultiWriteException(
                new StateMultiWriteResult(completed, completedPhysicalWrites),
                failedPlan.ResourceId,
                failedGroup.Select(static plan => plan.Source.Id),
                remainingGroups.SelectMany(static group => group.Select(plan => plan.Source.Id)),
                exception
            );
        }
    }

    /// <inheritdoc />
    public async ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        string sourceId,
        string targetId,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = EnterOperation();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        cancellationToken.ThrowIfCancellationRequested();

        var source = FindSource(sourceId);
        var target = FindSource(targetId);
        if (target.Writer is null)
        {
            throw new InvalidOperationException(
                $"State source '{target.Id}' does not support writes."
            );
        }

        if (!IsSourceActive(target.Id))
        {
            throw new InvalidOperationException(
                $"State source '{target.Id}' has been retired from this options instance."
            );
        }

        _logger?.LogInformation(
            MigrationEvent,
            "Migrating configuration contribution from source {SourceId} to {TargetSourceId} for {ModelType} options {OptionsName}.",
            source.Id,
            target.Id,
            typeof(TModel).FullName,
            _optionsName
        );
        var sourceResult = await ReadMigrationSourceAsync(source, cancellationToken)
            .ConfigureAwait(false);
        if (sourceResult.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Source '{source.Id}' could not be migrated: {sourceResult.Status}."
            );
        }

        var sourceFragment =
            sourceResult.Value
            ?? throw new InvalidOperationException(
                $"State source '{source.Id}' returned a null configuration fragment."
            );
        if (sourceResult.Schema is { } schema)
        {
            sourceFragment = await MigrateAsync(sourceFragment, schema, cancellationToken)
                .ConfigureAwait(false);
        }

        var currentSchema = TModel.ConfiglueSchema.ToMetadata();
        if (
            ReferenceEquals(source, target)
            && (sourceResult.Schema is null || sourceResult.Schema == currentSchema)
        )
        {
            return new StateSourceMigrationResult(
                source.Id,
                target.Id,
                sourceResult.Revision,
                sourceResult.Revision
            );
        }

        var targetResult = ReferenceEquals(source, target)
            ? sourceResult
            : await ReadMigrationSourceAsync(target, cancellationToken).ConfigureAwait(false);
        if (targetResult.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException($"Target source '{target.Id}' is unavailable.");
        }

        if (targetResult.Status == StateReadStatus.Success && targetResult.Value is null)
        {
            throw new InvalidOperationException(
                $"State source '{target.Id}' returned a null configuration fragment."
            );
        }

        var write = await WriteStateAsync(
                target,
                target.Writer,
                new StateWriteRequest<TFragment>(
                    sourceFragment,
                    targetResult.Revision,
                    CheckRevision: true
                ),
                "source migration",
                cancellationToken,
                source.Id,
                MigrationEvent
            )
            .ConfigureAwait(false);
        var migrationResult = new StateSourceMigrationResult(
            source.Id,
            target.Id,
            sourceResult.Revision,
            write.Revision
        );
        _logger?.LogInformation(
            MigrationEvent,
            "Migrated configuration contribution from source {SourceId} to {TargetSourceId} for {ModelType} options {OptionsName}.",
            source.Id,
            target.Id,
            typeof(TModel).FullName,
            _optionsName
        );
        return migrationResult;
    }

    /// <summary>
    /// Migrates selected source contributions to one or more projected targets. Successful targets are
    /// re-read and verified; repeating the operation skips targets already holding the requested fragment.
    /// </summary>
    public async ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<string> sourceIds,
        IReadOnlyDictionary<string, Func<TFragment, TFragment>> targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    )
    {
        using var operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(targetProjections);
        cancellationToken.ThrowIfCancellationRequested();

        var requestedSourceIds = sourceIds.ToArray();
        if (requestedSourceIds.Length == 0)
        {
            throw new ArgumentException(
                "At least one source must be selected for migration.",
                nameof(sourceIds)
            );
        }

        if (requestedSourceIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Source IDs cannot be empty.", nameof(sourceIds));
        }

        if (
            requestedSourceIds.Distinct(StringComparer.Ordinal).Count() != requestedSourceIds.Length
        )
        {
            throw new ArgumentException("A source can only be selected once.", nameof(sourceIds));
        }

        if (targetProjections.Count == 0)
        {
            throw new ArgumentException(
                "At least one target projection is required.",
                nameof(targetProjections)
            );
        }

        foreach (var target in targetProjections)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(target.Key);
            ArgumentNullException.ThrowIfNull(target.Value);
        }

        object? resolvedBeforeMigration = null;
        if (retireSources)
        {
            var before = await ResolveCoreAsync(null, cancellationToken).ConfigureAwait(false);
            if (before.Result.Status != StateReadStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Configuration state could not be read before source retirement: {before.Result.Status}."
                );
            }

            resolvedBeforeMigration = before.Result.Value;
        }

        var selectedIds = requestedSourceIds.ToHashSet(StringComparer.Ordinal);
        var overlappingTarget = targetProjections.Keys.FirstOrDefault(selectedIds.Contains);
        if (overlappingTarget is not null)
        {
            throw new ArgumentException(
                $"Target '{overlappingTarget}' is also a selected source. Use MigrateSourceAsync for an in-place source migration.",
                nameof(targetProjections)
            );
        }

        var sourceContributions =
            new List<(
                StateSource<TFragment> Source,
                StateReadResult<TFragment> Result,
                TFragment Fragment
            )>();
        var sourceRevisions = new List<StateRevision>();
        _logger?.LogInformation(
            MigrationEvent,
            "Starting storage migration for {ModelType} options {OptionsName} from sources {SourceIds} to targets {TargetSourceIds}.",
            typeof(TModel).FullName,
            _optionsName,
            string.Join(",", requestedSourceIds),
            string.Join(",", targetProjections.Keys)
        );
        foreach (var source in _sourceSet.Sources.Where(source => selectedIds.Contains(source.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ReadMigrationSourceAsync(source, cancellationToken)
                .ConfigureAwait(false);
            sourceRevisions.Add(new StateRevision(source.Id, result.Revision));
            if (result.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Source '{source.Id}' could not be migrated because it is unavailable."
                );
            }

            var fragment = result.Status switch
            {
                StateReadStatus.NotFound => TFragment.Empty,
                StateReadStatus.Success => result.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{source.Id}' returned a null configuration fragment."
                    ),
                _ => throw new InvalidOperationException(
                    $"Source '{source.Id}' could not be migrated: {result.Status}."
                ),
            };
            if (result.Schema is { } schema)
            {
                fragment = await MigrateAsync(fragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            sourceContributions.Add((source, result, fragment));
        }

        if (sourceContributions.Count != requestedSourceIds.Length)
        {
            var resolvedIds = sourceContributions
                .Select(static contribution => contribution.Source.Id)
                .ToHashSet(StringComparer.Ordinal);
            var missingId = requestedSourceIds.First(id => !resolvedIds.Contains(id));
            throw new InvalidOperationException($"State source '{missingId}' is not registered.");
        }

        var merged = TFragment.Empty;
        for (var index = sourceContributions.Count - 1; index >= 0; index--)
        {
            merged = merged.Merge(sourceContributions[index].Fragment);
        }

        var currentSchema = TModel.ConfiglueSchema.ToMetadata();
        async ValueTask VerifySourceSnapshotsAsync()
        {
            for (var index = 0; index < sourceContributions.Count; index++)
            {
                var contribution = sourceContributions[index];
                var latest = await ReadMigrationSourceAsync(contribution.Source, cancellationToken)
                    .ConfigureAwait(false);
                if (
                    latest.Status == contribution.Result.Status
                    && latest.Schema == contribution.Result.Schema
                    && string.Equals(
                        latest.Revision,
                        contribution.Result.Revision,
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }

                if (
                    latest.Status != contribution.Result.Status
                    || latest.Schema != contribution.Result.Schema
                )
                {
                    throw LogConflict(
                        $"Source '{contribution.Source.Id}' changed while the storage migration was running."
                    );
                }

                var latestFragment = latest.Status switch
                {
                    StateReadStatus.NotFound => TFragment.Empty,
                    StateReadStatus.Success => latest.Value
                        ?? throw new InvalidOperationException(
                            $"State source '{contribution.Source.Id}' returned a null configuration fragment."
                        ),
                    _ => throw LogConflict(
                        $"Source '{contribution.Source.Id}' became unavailable during migration."
                    ),
                };
                if (latest.Schema is { } latestSchema)
                {
                    latestFragment = await MigrateAsync(
                            latestFragment,
                            latestSchema,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }

                if (!ConfiglueFragmentComparer.AreEqual(latestFragment, contribution.Fragment))
                {
                    throw LogConflict(
                        $"Source '{contribution.Source.Id}' changed while the storage migration was running."
                    );
                }

                sourceContributions[index] = (contribution.Source, latest, latestFragment);
                sourceRevisions[index] = new StateRevision(contribution.Source.Id, latest.Revision);
            }
        }

        var targetPlans = new List<(
            StateSource<TFragment> Target,
            IStateWriter<TFragment> Writer,
            TFragment Desired
        )>(targetProjections.Count);
        foreach (var (targetId, project) in targetProjections)
        {
            var target = FindSource(targetId);
            if (!IsSourceActive(target.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{target.Id}' has been retired from this options instance."
                );
            }

            if (target.Writer is null)
            {
                throw new InvalidOperationException(
                    $"State source '{target.Id}' does not support writes."
                );
            }

            var desired =
                project(merged)
                ?? throw new InvalidOperationException(
                    $"The migration projection for target '{target.Id}' returned null."
                );
            targetPlans.Add((target, target.Writer, desired));
        }

        var targetResults = new List<StateStorageMigrationTargetResult>(targetPlans.Count);
        foreach (var (target, writer, desired) in targetPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await VerifySourceSnapshotsAsync().ConfigureAwait(false);
            var current = await ReadMigrationSourceAsync(target, cancellationToken)
                .ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException($"Target source '{target.Id}' is unavailable.");
            }

            var currentFragment = current.Status switch
            {
                StateReadStatus.NotFound => TFragment.Empty,
                StateReadStatus.Success => current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{target.Id}' returned a null configuration fragment."
                    ),
                _ => throw new InvalidOperationException(
                    $"Target source '{target.Id}' could not be read: {current.Status}."
                ),
            };
            if (current.Schema is { } targetSchema)
            {
                currentFragment = await MigrateAsync(
                        currentFragment,
                        targetSchema,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            var targetSchemaIsCurrent = current.Schema is null || current.Schema == currentSchema;
            var targetIsAlreadyCurrent =
                current.Status == StateReadStatus.Success
                || (current.Status == StateReadStatus.NotFound && desired.IsEmpty);
            if (
                targetIsAlreadyCurrent
                && targetSchemaIsCurrent
                && ConfiglueFragmentComparer.AreEqual(currentFragment, desired)
            )
            {
                var confirmation = await ReadMigrationSourceAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                if (
                    confirmation.Status != current.Status
                    || !string.Equals(
                        confirmation.Revision,
                        current.Revision,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw LogConflict(
                        $"Target source '{target.Id}' changed during migration verification."
                    );
                }

                if (confirmation.Status == StateReadStatus.Success)
                {
                    var confirmedFragment =
                        confirmation.Value
                        ?? throw new InvalidOperationException(
                            $"State source '{target.Id}' returned a null configuration fragment."
                        );
                    if (confirmation.Schema is { } confirmationSchema)
                    {
                        confirmedFragment = await MigrateAsync(
                                confirmedFragment,
                                confirmationSchema,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }

                    if (
                        (
                            confirmation.Schema is { } confirmedSchema
                            && confirmedSchema != currentSchema
                        ) || !ConfiglueFragmentComparer.AreEqual(confirmedFragment, desired)
                    )
                    {
                        throw LogConflict(
                            $"Target source '{target.Id}' changed during migration verification."
                        );
                    }
                }

                await VerifySourceSnapshotsAsync().ConfigureAwait(false);
                _logger?.LogInformation(
                    MigrationEvent,
                    "Storage migration target {TargetSourceId} already contains the verified contribution for {ModelType} options {OptionsName}.",
                    target.Id,
                    typeof(TModel).FullName,
                    _optionsName
                );
                targetResults.Add(
                    new StateStorageMigrationTargetResult(
                        target.Id,
                        current.Revision,
                        current.Revision,
                        WasAlreadyCurrent: true
                    )
                );
                continue;
            }

            await VerifySourceSnapshotsAsync().ConfigureAwait(false);
            var write = await WriteStateAsync(
                    target,
                    writer,
                    new StateWriteRequest<TFragment>(
                        desired,
                        current.Revision,
                        CheckRevision: true
                    ),
                    "storage migration",
                    cancellationToken,
                    string.Join(",", sourceContributions.Select(static item => item.Source.Id)),
                    MigrationEvent
                )
                .ConfigureAwait(false);
            var verification = await ReadMigrationSourceAsync(target, cancellationToken)
                .ConfigureAwait(false);
            if (
                verification.Status != StateReadStatus.Success
                || !string.Equals(verification.Revision, write.Revision, StringComparison.Ordinal)
            )
            {
                throw LogConflict(
                    $"Target source '{target.Id}' changed before migration verification completed."
                );
            }

            var verifiedFragment =
                verification.Value
                ?? throw new InvalidOperationException(
                    $"State source '{target.Id}' returned a null configuration fragment after migration."
                );
            if (verification.Schema is { } verificationSchema)
            {
                verifiedFragment = await MigrateAsync(
                        verifiedFragment,
                        verificationSchema,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            if (
                (verification.Schema is { } actualSchema && actualSchema != currentSchema)
                || !ConfiglueFragmentComparer.AreEqual(verifiedFragment, desired)
            )
            {
                throw LogConflict(
                    $"Target source '{target.Id}' did not retain the migrated fragment."
                );
            }

            _logger?.LogInformation(
                MigrationEvent,
                "Verified storage migration target {TargetSourceId} for {ModelType} options {OptionsName} from sources {SourceIds}.",
                target.Id,
                typeof(TModel).FullName,
                _optionsName,
                string.Join(",", sourceContributions.Select(static item => item.Source.Id))
            );

            targetResults.Add(
                new StateStorageMigrationTargetResult(
                    target.Id,
                    current.Revision,
                    write.Revision,
                    WasAlreadyCurrent: false
                )
            );
        }

        string[] retiredSourceIds = [];
        if (retireSources)
        {
            await VerifySourceSnapshotsAsync().ConfigureAwait(false);
            await VerifyRetirementPreservesResolvedModelAsync(
                    resolvedBeforeMigration!,
                    sourceContributions,
                    targetPlans,
                    targetResults,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await VerifySourceSnapshotsAsync().ConfigureAwait(false);
            retiredSourceIds = sourceContributions
                .Select(static contribution => contribution.Source.Id)
                .ToArray();
            RetireSourcesFromOptions(retiredSourceIds);
            _logger?.LogInformation(
                MigrationEvent,
                "Retired migrated sources {SourceIds} from {ModelType} options {OptionsName}.",
                string.Join(",", retiredSourceIds),
                typeof(TModel).FullName,
                _optionsName
            );
        }

        _logger?.LogInformation(
            MigrationEvent,
            "Completed storage migration for {ModelType} options {OptionsName} from sources {SourceIds} to targets {TargetSourceIds}; retired {RetiredSourceIds}.",
            typeof(TModel).FullName,
            _optionsName,
            string.Join(",", sourceContributions.Select(static item => item.Source.Id)),
            string.Join(",", targetPlans.Select(static item => item.Target.Id)),
            string.Join(",", retiredSourceIds)
        );

        return new StateStorageMigrationResult(
            sourceContributions.Select(static contribution => contribution.Source.Id),
            new StateRevisionVector(sourceRevisions),
            targetResults,
            retiredSourceIds
        );
    }

    /// <inheritdoc />
    public ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<string> sourceIds,
        IReadOnlyDictionary<string, Func<IConfiglueFragment, IConfiglueFragment>> targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    )
    {
        ArgumentNullException.ThrowIfNull(targetProjections);
        foreach (var target in targetProjections)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(target.Key);
            ArgumentNullException.ThrowIfNull(target.Value);
        }

        var typedProjections = targetProjections.ToDictionary(
            static pair => pair.Key,
            static pair =>
                (Func<TFragment, TFragment>)(
                    fragment =>
                        pair.Value(fragment) is TFragment projected
                            ? projected
                            : throw new InvalidOperationException(
                                $"The migration projection for target '{pair.Key}' returned an incompatible fragment."
                            )
                ),
            StringComparer.Ordinal
        );
        return MigrateSourcesToTargetsAsync(
            sourceIds,
            typedProjections,
            cancellationToken,
            retireSources
        );
    }

    private async ValueTask VerifyRetirementPreservesResolvedModelAsync(
        object baselineModel,
        IReadOnlyList<(
            StateSource<TFragment> Source,
            StateReadResult<TFragment> Result,
            TFragment Fragment
        )> sourceContributions,
        IReadOnlyList<(
            StateSource<TFragment> Target,
            IStateWriter<TFragment> Writer,
            TFragment Desired
        )> targetPlans,
        IReadOnlyList<StateStorageMigrationTargetResult> targetResults,
        CancellationToken cancellationToken
    )
    {
        var currentSchema = TModel.ConfiglueSchema.ToMetadata();
        var replacements = new Dictionary<string, StateReadResult<TFragment>>(
            StringComparer.Ordinal
        );
        foreach (var (source, result, _) in sourceContributions)
        {
            replacements.Add(
                source.Id,
                StateReadResult<TFragment>
                    .Success(TFragment.Empty, result.Revision, currentSchema)
                    .FromSource(source.Id, source.PhysicalOrigin)
            );
        }

        foreach (var (target, _, desired) in targetPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = targetResults.First(result =>
                string.Equals(result.TargetId, target.Id, StringComparison.Ordinal)
            );
            var current = (
                await target.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ).FromSource(target.Id, target.PhysicalOrigin);
            if (
                current.Status == StateReadStatus.Unavailable
                || !string.Equals(
                    current.Revision,
                    outcome.TargetRevision,
                    StringComparison.Ordinal
                )
            )
            {
                throw LogConflict($"Target source '{target.Id}' changed before source retirement.");
            }

            var currentFragment = current.Status switch
            {
                StateReadStatus.NotFound when desired.IsEmpty => TFragment.Empty,
                StateReadStatus.Success => current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{target.Id}' returned a null configuration fragment."
                    ),
                _ => throw LogConflict(
                    $"Target source '{target.Id}' is not available for source retirement."
                ),
            };
            if (current.Schema is { } schema)
            {
                currentFragment = await MigrateAsync(currentFragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (
                (current.Schema is { } actualSchema && actualSchema != currentSchema)
                || !ConfiglueFragmentComparer.AreEqual(currentFragment, desired)
            )
            {
                throw LogConflict(
                    $"Target source '{target.Id}' no longer contains the verified migration result."
                );
            }

            replacements.Add(
                target.Id,
                StateReadResult<TFragment>
                    .Success(desired, current.Revision, currentSchema)
                    .FromSource(target.Id, target.PhysicalOrigin)
            );
        }

        var proposed = await ResolveCoreAsync(replacements, cancellationToken)
            .ConfigureAwait(false);
        if (
            proposed.Result.Status != StateReadStatus.Success
            || baselineModel is not TModel before
            || !TModel.Diff(before, proposed.Result.Value!).IsEmpty
        )
        {
            throw LogConflict(
                "The migrated targets cannot replace the selected sources without changing the effective configuration."
            );
        }

        Validate(proposed.Result.Value!);
        foreach (var (target, _, desired) in targetPlans)
        {
            var outcome = targetResults.First(result =>
                string.Equals(result.TargetId, target.Id, StringComparison.Ordinal)
            );
            var latest = (
                await target.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ).FromSource(target.Id, target.PhysicalOrigin);
            if (!string.Equals(latest.Revision, outcome.TargetRevision, StringComparison.Ordinal))
            {
                throw LogConflict(
                    $"Target source '{target.Id}' changed while source retirement was being verified."
                );
            }

            if (latest.Status == StateReadStatus.NotFound && desired.IsEmpty)
            {
                continue;
            }

            if (latest.Status != StateReadStatus.Success || latest.Value is null)
            {
                throw LogConflict(
                    $"Target source '{target.Id}' is not available for source retirement."
                );
            }

            var latestFragment = latest.Schema is { } latestSchema
                ? await MigrateAsync(latest.Value, latestSchema, cancellationToken)
                    .ConfigureAwait(false)
                : latest.Value;
            if (
                (latest.Schema is { } actualSchema && actualSchema != currentSchema)
                || !ConfiglueFragmentComparer.AreEqual(latestFragment, desired)
            )
            {
                throw LogConflict(
                    $"Target source '{target.Id}' no longer contains the verified migration result."
                );
            }
        }
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
            _reloadFailureListeners.Clear();
            _watchCancellation?.Cancel();
        }

        IDisposable? currentValueCacheSubscription;
        lock (_currentValueGate)
        {
            currentValueCacheSubscription = _currentValueCacheSubscription;
            _currentValueCacheSubscription = null;
            Volatile.Write(ref _currentValueCache, null);
        }
        currentValueCacheSubscription?.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task? watchTask;
        Task operationsDrained;
        lock (_changeGate)
        {
            watchTask = _watchTask;
            operationsDrained = _operationsDrained.Task;
        }

        List<Exception>? errors = null;
        if (watchTask is not null)
        {
            try
            {
                await watchTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }
        try
        {
            await operationsDrained.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (errors ??= []).Add(exception);
        }
        _watchCancellation?.Dispose();
        if (errors is not null)
            throw new AggregateException("Options shutdown failed.", errors);
    }

    private IDisposable EnterOperation()
    {
        for (var frame = _operationFrame.Value; frame is not null; frame = frame.Parent)
        {
            if (ReferenceEquals(frame.Owner, this) && Volatile.Read(ref frame.Active) != 0)
            {
                return new OperationLease(this, frame: null);
            }
        }
        lock (_changeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeOperations++ == 0)
            {
                _operationsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
            }
        }
        var root = new OperationFrame(this, _operationFrame.Value);
        _operationFrame.Value = root;
        return new OperationLease(this, root);
    }

    private void ExitOperation(OperationFrame? root)
    {
        if (root is null)
            return;
        Volatile.Write(ref root.Active, 0);
        if (ReferenceEquals(_operationFrame.Value, root))
            _operationFrame.Value = root.Parent;
        lock (_changeGate)
        {
            if (--_activeOperations == 0)
                _operationsDrained.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedOperationsSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private sealed class OperationFrame(
        ConfiglueOptions<TModel, TFragment> owner,
        OperationFrame? parent
    )
    {
        public ConfiglueOptions<TModel, TFragment> Owner { get; } = owner;
        public OperationFrame? Parent { get; } = parent;
        public int Active = 1;
    }

    private sealed class OperationLease(
        ConfiglueOptions<TModel, TFragment> owner,
        OperationFrame? frame
    ) : IDisposable
    {
        private ConfiglueOptions<TModel, TFragment>? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitOperation(frame);
    }

    private StateSource<TFragment> SelectWriteSource()
    {
        var activeSources = GetActiveSources();
        var source = _writeRoute.SourceId is { } id
            ? activeSources.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal)
            )
            : activeSources.FirstOrDefault(static candidate => candidate.Writer is not null);

        if (source is null)
        {
            if (
                _writeRoute.SourceId is { } retiredId
                && _sourceSet.Sources.Any(candidate =>
                    string.Equals(candidate.Id, retiredId, StringComparison.Ordinal)
                )
            )
            {
                throw new InvalidOperationException(
                    $"State source '{retiredId}' has been retired from this options instance."
                );
            }

            throw new InvalidOperationException(
                _writeRoute.SourceId is { } sourceId
                    ? $"State source '{sourceId}' is not registered."
                    : "No writable state source is registered."
            );
        }

        if (source.Writer is null)
        {
            throw new InvalidOperationException(
                $"State source '{source.Id}' does not support writes."
            );
        }

        return source;
    }

    private async ValueTask<StateWriteResult> WriteStateAsync(
        StateSource<TFragment> target,
        IStateWriter<TFragment> writer,
        StateWriteRequest<TFragment> request,
        string operation,
        CancellationToken cancellationToken,
        string? relatedSourceIds = null,
        EventId? eventId = null
    )
    {
        var writeEvent = eventId ?? PhysicalWriteEvent;
        _logger?.LogInformation(
            writeEvent,
            "{Operation} for {ModelType} options {OptionsName} through source {SourceId} at resource {ResourceId}; related sources {RelatedSourceIds}.",
            operation,
            typeof(TModel).FullName,
            _optionsName,
            target.Id,
            target.ResourceId?.Value,
            relatedSourceIds
        );
        StateWriteResult result;
        try
        {
            result = await writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Preserve the writer's exception type for callers that classify conflicts or retries.
#pragma warning disable S2139
        catch (Exception exception)
        {
            _logger?.LogError(
                writeEvent,
                exception,
                "{Operation} failed for {ModelType} options {OptionsName} through source {SourceId} at resource {ResourceId}; related sources {RelatedSourceIds}.",
                operation,
                typeof(TModel).FullName,
                _optionsName,
                target.Id,
                target.ResourceId?.Value,
                relatedSourceIds
            );
            throw;
        }
#pragma warning restore S2139

        _logger?.LogDebug(
            writeEvent,
            "{Operation} completed through source {SourceId} at resource {ResourceId} for {ModelType} options {OptionsName}.",
            operation,
            target.Id,
            target.ResourceId?.Value,
            typeof(TModel).FullName,
            _optionsName
        );
        InvalidateCurrentValueCache();
        return result;
    }

    private async ValueTask<StateReadResult<TFragment>> ReadMigrationSourceAsync(
        StateSource<TFragment> source,
        CancellationToken cancellationToken
    )
    {
        StateReadResult<TFragment> result;
        try
        {
            result = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Preserve the reader's exception type so a codec's recovery policy remains effective.
#pragma warning disable S2139
        catch (Exception exception)
        {
            _logger?.LogError(
                MigrationEvent,
                exception,
                "Reading migration source {SourceId} failed for {ModelType} options {OptionsName} at {PhysicalOrigin} ({ResourceId}).",
                source.Id,
                typeof(TModel).FullName,
                _optionsName,
                source.PhysicalOrigin,
                source.ResourceId?.Value
            );
            throw;
        }
#pragma warning restore S2139

        var sourcedResult = result.FromSource(source.Id, source.PhysicalOrigin);
        _logger?.LogDebug(
            MigrationEvent,
            "Migration source {SourceId} returned {ReadStatus} for {ModelType} options {OptionsName} at {PhysicalOrigin} ({ResourceId}).",
            source.Id,
            sourcedResult.Status,
            typeof(TModel).FullName,
            _optionsName,
            source.PhysicalOrigin,
            source.ResourceId?.Value
        );
        return sourcedResult;
    }

    private StateConflictException LogConflict(string message)
    {
        var exception = new StateConflictException(message);
        _logger?.LogWarning(
            ConflictEvent,
            exception,
            "A configuration write conflict occurred for {ModelType} options {OptionsName}: {Conflict}.",
            typeof(TModel).FullName,
            _optionsName,
            message
        );
        return exception;
    }

    private StateSource<TFragment> FindSource(string sourceId) =>
        _sourceSet.Sources.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sourceId, StringComparison.Ordinal)
        ) ?? throw new InvalidOperationException($"State source '{sourceId}' is not registered.");

    private StateSource<TFragment>[] GetActiveSources()
    {
        lock (_sourceGate)
        {
            return _activeSources;
        }
    }

    private bool IsSourceActive(string sourceId)
    {
        lock (_sourceGate)
        {
            return !_retiredSourceIds.Contains(sourceId);
        }
    }

    private void RetireSourcesFromOptions(IEnumerable<string> sourceIds)
    {
        TaskCompletionSource? topologyChanged = null;
        lock (_sourceGate)
        {
            var changed = false;
            foreach (var sourceId in sourceIds)
            {
                changed |= _retiredSourceIds.Add(sourceId);
            }

            if (changed)
            {
                _activeSources = _sourceSet
                    .Sources.Where(source => !_retiredSourceIds.Contains(source.Id))
                    .ToArray();
                topologyChanged = _sourceTopologyChanged;
                _sourceTopologyChanged = NewTopologySignal();
            }
        }

        topologyChanged?.TrySetResult();
    }

    private void ValidateWritePlan(StateWritePlan writePlan)
    {
        foreach (var (propertyPath, sourceId) in writePlan.PropertyRoutes)
        {
            var path = propertyPath.Split('.', StringSplitOptions.None);
            var schema = TModel.ConfiglueSchema;
            for (var index = 0; index < path.Length; index++)
            {
                var member = schema.Members.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, path[index], StringComparison.Ordinal)
                );
                if (string.IsNullOrEmpty(member.Name))
                {
                    throw new ArgumentException(
                        $"Write plan path '{propertyPath}' refers to unknown member '{path[index]}' in '{schema.Id}'.",
                        nameof(writePlan)
                    );
                }

                if (index < path.Length - 1)
                {
                    schema =
                        member.NestedSchemaFactory?.Invoke()
                        ?? throw new ArgumentException(
                            $"Write plan path '{propertyPath}' continues through non-nested member '{member.Name}'.",
                            nameof(writePlan)
                        );
                }
            }

            var source = FindSource(sourceId);
            if (!IsSourceActive(source.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' has been retired from this options instance."
                );
            }

            if (source.Writer is null)
            {
                throw new InvalidOperationException(
                    $"State source '{sourceId}' does not support writes."
                );
            }
        }
    }

    private async ValueTask<StateWriteResult> WriteChangesToSourcesAsync(
        StateSource<TFragment> fallbackSource,
        TModel before,
        TModel after,
        string? expectedFallbackRevision,
        StateRevisionVector? expectedBaselineRevisions,
        IReadOnlyList<ResolvedContribution> baselineContributions,
        StateWritePlan writePlan,
        CancellationToken cancellationToken
    )
    {
        using var operation = EnterOperation();
        after = CloneModel(after);
        Validate(after);
        var changes = TModel.Diff(before, after);
        if (changes.IsEmpty)
        {
            return new StateWriteResult(expectedFallbackRevision);
        }

        var canSearchFallbackCandidates = _writeRoute.SourceId is null;
        var initialRouting = PartitionRoutedChanges(
            TModel.ConfiglueSchema,
            changes,
            after,
            [],
            fallbackSource.Id,
            writePlan
        );
        var hasUnroutedChanges = initialRouting.ContainsKey(fallbackSource.Id);
        var fallbackCandidateIds =
            canSearchFallbackCandidates && hasUnroutedChanges
                ? GetActiveSources()
                    .Where(static candidate => candidate.Writer is not null)
                    .Select(static candidate => candidate.Id)
                    .ToArray()
                : [fallbackSource.Id];
        StateSourcePatch[]? patches = null;
        var selectedFallbackSourceId = fallbackSource.Id;
        string? lastFailure = null;
        foreach (var candidateId in fallbackCandidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var routedChanges = string.Equals(
                candidateId,
                fallbackSource.Id,
                StringComparison.Ordinal
            )
                ? initialRouting
                : PartitionRoutedChanges(
                    TModel.ConfiglueSchema,
                    changes,
                    after,
                    [],
                    candidateId,
                    writePlan
                );

            StateSourcePatch[] candidatePatches;
            try
            {
                candidatePatches = CreateRoutedPatches(routedChanges, after, baselineContributions);
            }
            catch (StateConflictException exception) when (canSearchFallbackCandidates)
            {
                lastFailure = exception.Message;
                continue;
            }

            if (candidatePatches.Length == 0)
            {
                return new StateWriteResult(expectedFallbackRevision);
            }

            if (
                canSearchFallbackCandidates
                && hasUnroutedChanges
                && !await CanRealizePatchBatchAsync(
                        candidatePatches,
                        expectedBaselineRevisions,
                        after,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                lastFailure =
                    $"Candidate source '{candidateId}' cannot realize the requested edit.";
                continue;
            }

            patches = candidatePatches;
            selectedFallbackSourceId = candidateId;
            break;
        }

        if (patches is null)
        {
            throw LogConflict(
                lastFailure is null
                    ? "No writable source candidate could realize the routed configuration edit."
                    : $"No writable source candidate could realize the routed configuration edit. {lastFailure}"
            );
        }

        var result = await ApplyPatchesCoreAsync(
                patches,
                expectedBaselineRevisions,
                after,
                cancellationToken
            )
            .ConfigureAwait(false);
        var fallbackResult = result.Sources.FirstOrDefault(source =>
            string.Equals(source.SourceId, selectedFallbackSourceId, StringComparison.Ordinal)
        );
        var revision = fallbackResult.SourceId is not null
            ? fallbackResult.Revision
            : result.Sources[0].Revision;
        return new StateWriteResult(revision) { MultiWriteResult = result };
    }

    private StateSourcePatch[] CreateRoutedPatches(
        Dictionary<string, IConfiglueFragment> routedChanges,
        TModel after,
        IReadOnlyList<ResolvedContribution> baselineContributions
    )
    {
        var patches = new List<StateSourcePatch>(routedChanges.Count);
        foreach (var (sourceId, sourceChanges) in routedChanges)
        {
            var plannedChanges = (TFragment)PlanMergeAwareChanges(
                TModel.ConfiglueSchema,
                sourceChanges,
                after,
                [],
                sourceId,
                baselineContributions
            );
            if (!plannedChanges.IsEmpty)
            {
                patches.Add(
                    new StateSourcePatch(sourceId, new FragmentChangesPatch(plannedChanges))
                );
            }
        }

        return patches.ToArray();
    }

    private async ValueTask<bool> CanRealizePatchBatchAsync(
        StateSourcePatch[] patchRequests,
        StateRevisionVector? expectedBaselineRevisions,
        TModel expectedResolvedModel,
        CancellationToken cancellationToken
    )
    {
        var baseline = await ResolveCoreAsync(null, cancellationToken).ConfigureAwait(false);
        if (baseline.Result.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read before planning writes: {baseline.Result.Status}."
            );
        }

        if (
            expectedBaselineRevisions is not null
            && !HaveSameRevisions(expectedBaselineRevisions, baseline.Result.Revisions)
        )
        {
            throw LogConflict("A state source changed after the configuration edit began.");
        }

        var replacements = new Dictionary<string, StateReadResult<TFragment>>(
            StringComparer.Ordinal
        );
        foreach (var patchRequest in patchRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = FindSource(patchRequest.SourceId);
            if (!IsSourceActive(source.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' has been retired from this options instance."
                );
            }

            var current = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                return false;
            }

            if (
                baseline.Result.Revisions is null
                || !baseline.Result.Revisions.TryGetRevision(source.Id, out var baselineRevision)
                || !string.Equals(current.Revision, baselineRevision, StringComparison.Ordinal)
            )
            {
                throw LogConflict(
                    $"State source '{source.Id}' changed while the write plan was being evaluated."
                );
            }

            var sourceFragment = current.Status switch
            {
                StateReadStatus.NotFound => TFragment.Empty,
                StateReadStatus.Success => current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{source.Id}' returned a null configuration fragment."
                    ),
                _ => throw new InvalidOperationException(
                    $"Source '{source.Id}' could not be planned: {current.Status}."
                ),
            };
            if (current.Schema is { } schema)
            {
                sourceFragment = await MigrateAsync(sourceFragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (patchRequest.Patch.Apply(sourceFragment) is not TFragment patchedFragment)
            {
                throw new InvalidOperationException(
                    $"The patch for source '{source.Id}' returned an incompatible fragment."
                );
            }

            replacements.Add(
                source.Id,
                StateReadResult<TFragment>.Success(
                    patchedFragment,
                    current.Revision,
                    TModel.ConfiglueSchema.ToMetadata()
                )
            );
        }

        var proposed = await ResolveCoreAsync(replacements, cancellationToken)
            .ConfigureAwait(false);
        if (proposed.Result.Status != StateReadStatus.Success)
        {
            return false;
        }

        if (!HaveSameRevisions(baseline.Result.Revisions, proposed.Result.Revisions))
        {
            throw LogConflict("A state source changed while the write plan was being evaluated.");
        }

        if (!TModel.Diff(proposed.Result.Value!, expectedResolvedModel).IsEmpty)
        {
            return false;
        }

        Validate(proposed.Result.Value!);
        return true;
    }

    private static List<string> GetChangedPropertyPaths(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        List<string> path
    )
    {
        var paths = new List<string>();
        foreach (var change in changes.EnumeratePresentMembers())
        {
            var member = schema.Members.FirstOrDefault(candidate => candidate.Id == change.Id);
            if (string.IsNullOrEmpty(member.Name))
            {
                continue;
            }

            path.Add(member.Name);
            if (member.NestedSchemaFactory is not null && change.Value is IConfiglueFragment nested)
            {
                paths.AddRange(GetChangedPropertyPaths(member.NestedSchemaFactory(), nested, path));
            }
            else
            {
                paths.Add(string.Join('.', path));
            }

            path.RemoveAt(path.Count - 1);
        }

        return paths;
    }

    private Dictionary<string, IConfiglueFragment> PartitionRoutedChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        object afterModel,
        List<string> path,
        string fallbackSourceId,
        StateWritePlan writePlan
    )
    {
        var routed = new Dictionary<string, IConfiglueFragment>(StringComparer.Ordinal);
        foreach (var change in changes.EnumeratePresentMembers())
        {
            var member = schema.Members.FirstOrDefault(candidate => candidate.Id == change.Id);
            if (string.IsNullOrEmpty(member.Name))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.Id}' has no member with id {change.Id}."
                );
            }

            path.Add(member.Name);
            try
            {
                var propertyPath = string.Join('.', path);
                var afterValue = member.GetValue?.Invoke(afterModel);
                if (
                    member.NestedSchemaFactory is not null
                    && change.Value is IConfiglueFragment nestedChanges
                    && afterValue is not null
                )
                {
                    var nestedRouted = PartitionRoutedChanges(
                        member.NestedSchemaFactory(),
                        nestedChanges,
                        afterValue,
                        path,
                        fallbackSourceId,
                        writePlan
                    );
                    foreach (var (sourceId, nestedFragment) in nestedRouted)
                    {
                        var sourceFragment = routed.TryGetValue(sourceId, out var current)
                            ? current
                            : schema.CreateEmptyFragment();
                        routed[sourceId] = sourceFragment.WithMember(member.Id, nestedFragment);
                    }

                    continue;
                }

                if (
                    member.NestedSchemaFactory is not null
                    && afterValue is null
                    && writePlan.HasRouteBelow(propertyPath)
                )
                {
                    throw LogConflict(
                        $"The edit replaces nested member '{propertyPath}' with null, so its more specific source routes cannot be applied."
                    );
                }

                var targetSourceId = writePlan.ResolveSourceId(propertyPath, fallbackSourceId);
                var targetFragment = routed.TryGetValue(targetSourceId, out var existing)
                    ? existing
                    : schema.CreateEmptyFragment();
                routed[targetSourceId] = targetFragment.WithMember(member.Id, change.Value);
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        return routed;
    }

    private Dictionary<string, IConfiglueFragment> PartitionCompositeChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        CompositeStateSource<TFragment> composite,
        List<string> path
    )
    {
        var routed = new Dictionary<string, IConfiglueFragment>(StringComparer.Ordinal);
        foreach (var change in changes.EnumeratePresentMembers().ToArray())
        {
            var member = schema.Members.FirstOrDefault(candidate => candidate.Id == change.Id);
            if (string.IsNullOrEmpty(member.Name))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.Id}' has no member with id {change.Id}."
                );
            }

            path.Add(member.Name);
            try
            {
                var propertyPath = string.Join('.', path);
                if (
                    member.NestedSchemaFactory is not null
                    && change.Value is IConfiglueFragment nestedChanges
                    && composite.HasWriteRouteBelow(propertyPath)
                )
                {
                    var nestedRouted = PartitionCompositeChanges(
                        member.NestedSchemaFactory(),
                        nestedChanges,
                        composite,
                        path
                    );
                    foreach (var (nestedComponentId, nestedFragment) in nestedRouted)
                    {
                        var componentChanges = routed.TryGetValue(
                            nestedComponentId,
                            out var existing
                        )
                            ? existing
                            : schema.CreateEmptyFragment();
                        routed[nestedComponentId] = componentChanges.WithMember(
                            member.Id,
                            nestedFragment
                        );
                    }

                    continue;
                }

                if (
                    member.NestedSchemaFactory is not null
                    && composite.HasWriteRouteBelow(propertyPath)
                )
                {
                    throw LogConflict(
                        $"The edit replaces nested member '{propertyPath}' as a whole, so its more specific composite component routes cannot be applied."
                    );
                }

                var targetComponentId = composite.ResolveWriteComponent(propertyPath).Id;
                var targetChanges = routed.TryGetValue(targetComponentId, out var current)
                    ? current
                    : schema.CreateEmptyFragment();
                routed[targetComponentId] = targetChanges.WithMember(member.Id, change.Value);
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        return routed;
    }

    private static bool IsValidMemberPath(
        ConfiglueModelSchema schema,
        string[] segments,
        int index = 0
    )
    {
        var member = schema.Members.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, segments[index], StringComparison.Ordinal)
        );
        if (string.IsNullOrEmpty(member.Name))
        {
            return false;
        }

        if (index == segments.Length - 1)
        {
            return true;
        }

        return member.NestedSchemaFactory is not null
            && IsValidMemberPath(member.NestedSchemaFactory(), segments, index + 1);
    }

    private async ValueTask<StateWriteResult> WriteChangesToSourceAsync(
        StateSource<TFragment> source,
        TModel before,
        TModel after,
        string? expectedRevision,
        IReadOnlyList<ResolvedContribution> baselineContributions,
        StateRevisionVector? expectedBaselineRevisions,
        CancellationToken cancellationToken
    )
    {
        using var operation = EnterOperation();
        after = CloneModel(after);
        Validate(after);
        var requestedChanges = TModel.Diff(before, after);
        if (requestedChanges.IsEmpty)
        {
            return new StateWriteResult(expectedRevision);
        }

        var searchCandidates = _writeRoute.SourceId is null;
        var candidates = searchCandidates
            ? GetActiveSources().Where(static candidate => candidate.Writer is not null).ToArray()
            : [source];
        string? lastFailure = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateExpectedRevision = expectedRevision;
            if (
                !string.Equals(candidate.Id, source.Id, StringComparison.Ordinal)
                && (
                    expectedBaselineRevisions is null
                    || !expectedBaselineRevisions.TryGetRevision(
                        candidate.Id,
                        out candidateExpectedRevision
                    )
                )
            )
            {
                throw LogConflict(
                    $"The edit baseline has no revision for candidate source '{candidate.Id}'."
                );
            }

            var current = await candidate.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (
                !string.Equals(
                    current.Revision,
                    candidateExpectedRevision,
                    StringComparison.Ordinal
                )
            )
            {
                throw LogConflict(
                    $"State source '{candidate.Id}' changed after the configuration edit began."
                );
            }

            if (current.Status == StateReadStatus.Unavailable)
            {
                lastFailure = $"Candidate source '{candidate.Id}' is unavailable.";
                if (searchCandidates)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Cannot safely update configuration because source '{candidate.Id}' is unavailable."
                );
            }

            var sourceFragment =
                current.Status == StateReadStatus.Success
                    ? current.Value
                        ?? throw new InvalidOperationException(
                            $"State source '{candidate.Id}' returned a null configuration fragment."
                        )
                    : TFragment.Empty;
            if (current.Schema is { } schema)
            {
                sourceFragment = await MigrateAsync(sourceFragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            TFragment plannedChanges;
            try
            {
                plannedChanges = (TFragment)PlanMergeAwareChanges(
                    TModel.ConfiglueSchema,
                    requestedChanges,
                    after,
                    [],
                    candidate.Id,
                    baselineContributions
                );
            }
            catch (StateConflictException exception) when (searchCandidates)
            {
                lastFailure = exception.Message;
                continue;
            }

            var updated = sourceFragment.ApplyChanges(plannedChanges);
            var proposed = await ReadCoreAsync(
                    new Dictionary<string, StateReadResult<TFragment>>(StringComparer.Ordinal)
                    {
                        [candidate.Id] = StateReadResult<TFragment>.Success(
                            updated,
                            current.Revision,
                            TModel.ConfiglueSchema.ToMetadata()
                        ),
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (proposed.Status != StateReadStatus.Success)
            {
                lastFailure = $"The proposal resolved to {proposed.Status}.";
                if (searchCandidates)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"The edited configuration could not be resolved: {proposed.Status}."
                );
            }

            if (!TModel.Diff(proposed.Value!, after).IsEmpty)
            {
                lastFailure =
                    $"Candidate source '{candidate.Id}' cannot realize the requested edit while preserving higher-priority contributions.";
                if (searchCandidates)
                {
                    continue;
                }

                throw LogConflict(lastFailure);
            }

            Validate(proposed.Value!);
            var latest = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (
                latest.Status != StateReadStatus.Success
                || !HaveSameRevisions(expectedBaselineRevisions, latest.Revisions)
            )
            {
                throw LogConflict(
                    "A state source changed before the configuration edit could be written."
                );
            }

            return await WriteStateAsync(
                    candidate,
                    candidate.Writer!,
                    new StateWriteRequest<TFragment>(
                        updated,
                        current.Revision,
                        CheckRevision: true
                    ),
                    "save",
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        throw LogConflict(
            lastFailure is null
                ? "No writable source candidate could realize the requested edit."
                : $"No writable source candidate could realize the requested edit. {lastFailure}"
        );
    }

    private IConfiglueFragment PlanMergeAwareChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        object? afterModel,
        List<string> path,
        string targetSourceId,
        IReadOnlyList<ResolvedContribution> contributions
    )
    {
        foreach (var change in changes.EnumeratePresentMembers().ToArray())
        {
            var member = schema.Members.FirstOrDefault(candidate => candidate.Id == change.Id);
            if (string.IsNullOrEmpty(member.Name))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.Id}' has no member with id {change.Id}."
                );
            }

            path.Add(member.Name);
            try
            {
                var afterValue = afterModel is null ? null : member.GetValue?.Invoke(afterModel);
                if (
                    member.NestedSchemaFactory is not null
                    && change.Value is IConfiglueFragment nestedChanges
                    && afterValue is not null
                )
                {
                    changes = changes.WithMember(
                        member.Id,
                        PlanMergeAwareChanges(
                            member.NestedSchemaFactory(),
                            nestedChanges,
                            afterValue,
                            path,
                            targetSourceId,
                            contributions
                        )
                    );
                    continue;
                }

                if (member.MergeStrategy is { } mergeStrategy)
                {
                    var strategySourceOrder = GetActiveSources().Reverse().ToArray();
                    var strategyValues = new ConfiglueMergeSourceValue[strategySourceOrder.Length];
                    for (var index = 0; index < strategySourceOrder.Length; index++)
                    {
                        var source = strategySourceOrder[index];
                        var value = Optional<object?>.Missing;
                        foreach (var contribution in contributions)
                        {
                            if (
                                string.Equals(
                                    contribution.Source.Id,
                                    source.Id,
                                    StringComparison.Ordinal
                                )
                                && contribution.Result.Value is { } sourceValue
                                && TryGetFragmentValue(sourceValue, path, out var contributionValue)
                            )
                            {
                                value = Optional<object?>.Present(contributionValue);
                                break;
                            }
                        }

                        strategyValues[index] = new ConfiglueMergeSourceValue(source.Id, value);
                    }

                    if (
                        !mergeStrategy.TryPlanSourceContribution(
                            strategyValues,
                            targetSourceId,
                            afterValue,
                            out var targetContribution,
                            out var reason
                        )
                    )
                    {
                        throw LogConflict(
                            reason
                                ?? $"The custom merge strategy cannot represent the edit to '{member.Name}' in source '{targetSourceId}'."
                        );
                    }

                    changes = targetContribution.IsPresent
                        ? changes.WithMember(member.Id, targetContribution.Value)
                        : changes.WithoutMember(member.Id);
                    continue;
                }

                if (
                    member.CollectionValueFactory is null
                    || afterValue is not System.Collections.IEnumerable desiredValues
                    || afterValue is string
                )
                {
                    continue;
                }

                var desired = desiredValues.Cast<object?>().ToList();
                var valuesBySource = GetCollectionContributions(path, contributions);
                var sourceOrder = GetActiveSources().Reverse().ToArray();
                var targetIndex = Array.FindIndex(
                    sourceOrder,
                    candidate =>
                        string.Equals(candidate.Id, targetSourceId, StringComparison.Ordinal)
                );
                if (targetIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"State source '{targetSourceId}' is not registered."
                    );
                }

                var targetValues = member.MergeMode switch
                {
                    MergeMode.Append => PlanAppendContribution(
                        sourceOrder,
                        targetIndex,
                        valuesBySource,
                        desired,
                        member.Name
                    ),
                    MergeMode.SetUnion => PlanSetUnionContribution(
                        sourceOrder,
                        targetIndex,
                        valuesBySource,
                        desired,
                        member
                    ),
                    _ => desired,
                };
                changes = changes.WithMember(
                    member.Id,
                    member.CollectionValueFactory(targetValues)
                );
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        return changes;
    }

    private static Dictionary<string, List<object?>> GetCollectionContributions(
        IReadOnlyList<string> path,
        IReadOnlyList<ResolvedContribution> contributions
    )
    {
        var valuesBySource = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        foreach (var contribution in contributions)
        {
            if (
                !TryGetFragmentValue(contribution.Result.Value!, path, out var value)
                || value is not System.Collections.IEnumerable values
                || value is string
            )
            {
                continue;
            }

            valuesBySource[contribution.Source.Id] = values.Cast<object?>().ToList();
        }

        return valuesBySource;
    }

    private List<object?> PlanAppendContribution(
        IReadOnlyList<StateSource<TFragment>> sourceOrder,
        int targetIndex,
        IReadOnlyDictionary<string, List<object?>> valuesBySource,
        IReadOnlyList<object?> desired,
        string memberName
    )
    {
        var prefix = new List<object?>();
        for (var index = 0; index < targetIndex; index++)
        {
            if (valuesBySource.TryGetValue(sourceOrder[index].Id, out var values))
            {
                prefix.AddRange(values);
            }
        }

        var suffix = new List<object?>();
        for (var index = targetIndex + 1; index < sourceOrder.Count; index++)
        {
            if (valuesBySource.TryGetValue(sourceOrder[index].Id, out var values))
            {
                suffix.AddRange(values);
            }
        }

        if (
            desired.Count < prefix.Count + suffix.Count
            || !desired.Take(prefix.Count).SequenceEqual(prefix)
            || !desired.Skip(desired.Count - suffix.Count).SequenceEqual(suffix)
        )
        {
            throw LogConflict(
                $"The edit to append-merged member '{memberName}' cannot be represented by source '{sourceOrder[targetIndex].Id}' while preserving other source contributions."
            );
        }

        return desired
            .Skip(prefix.Count)
            .Take(desired.Count - prefix.Count - suffix.Count)
            .ToList();
    }

    private List<object?> PlanSetUnionContribution(
        IReadOnlyList<StateSource<TFragment>> sourceOrder,
        int targetIndex,
        IReadOnlyDictionary<string, List<object?>> valuesBySource,
        IReadOnlyList<object?> desired,
        ConfiglueMemberSchema member
    )
    {
        var otherValues = new List<object?>();
        foreach (
            var values in sourceOrder
                .Where(
                    (source, index) => index != targetIndex && valuesBySource.ContainsKey(source.Id)
                )
                .Select(source => valuesBySource[source.Id])
        )
        {
            otherValues.AddRange(values.Where(value => !otherValues.Contains(value)));
        }

        if (otherValues.Any(value => !desired.Contains(value)))
        {
            throw LogConflict(
                $"The edit to set-union member '{member.Name}' removes a value contributed by another source."
            );
        }

        var targetValues = desired.Where(value => !otherValues.Contains(value)).ToList();
        var merged = new List<object?>();
        for (var index = 0; index < sourceOrder.Count; index++)
        {
            IEnumerable<object?> values;
            if (index == targetIndex)
            {
                values = targetValues;
            }
            else if (valuesBySource.TryGetValue(sourceOrder[index].Id, out var sourceValues))
            {
                values = sourceValues;
            }
            else
            {
                values = [];
            }

            merged.AddRange(values.Where(value => !merged.Contains(value)));
        }

        var isSet =
            member.ValueType.IsGenericType
            && (
                member.ValueType.GetGenericTypeDefinition() == typeof(ISet<>)
                || member.ValueType.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)
                || member.ValueType.GetGenericTypeDefinition() == typeof(HashSet<>)
            );
        var matchesDesired = isSet
            ? merged.Count == desired.Distinct().Count() && merged.All(desired.Contains)
            : merged.SequenceEqual(desired);
        if (!matchesDesired)
        {
            throw LogConflict(
                $"The edit to set-union member '{member.Name}' cannot be represented by source '{sourceOrder[targetIndex].Id}' while preserving other source contributions."
            );
        }

        return targetValues;
    }

    private void Validate(TModel value)
    {
        var failures = new List<string>();
        foreach (var validator in _validators)
        {
            failures.AddRange(validator.Validate(_optionsName, value));
        }

        if (_validateDataAnnotations && RuntimeFeature.IsDynamicCodeSupported)
        {
            var validationResults = new List<ValidationResult>();
            Validator.TryValidateObject(
                value,
                new ValidationContext(value),
                validationResults,
                validateAllProperties: true
            );
            failures.AddRange(
                validationResults.Select(result =>
                    result.ErrorMessage ?? "Configuration validation failed."
                )
            );
        }

        if (failures.Count > 0)
        {
            throw new ConfiglueValidationException(_optionsName, typeof(TModel), failures);
        }
    }

    private async ValueTask<TFragment> MigrateAsync(
        TFragment value,
        StateSchemaMetadata sourceSchema,
        CancellationToken cancellationToken
    ) =>
        await _migrationChain
            .MigrateAsync(value, sourceSchema, cancellationToken)
            .ConfigureAwait(false);

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

                await WaitForAnyChangeAsync(previous.Revisions, cancellationToken)
                    .ConfigureAwait(false);
                if (_onChangeDebounce > TimeSpan.Zero)
                {
                    await Task.Delay(_onChangeDebounce, cancellationToken).ConfigureAwait(false);
                }

                var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
                if (
                    current.Status == StateReadStatus.Success
                    && !HaveSameRevisions(previous.Revisions, current.Revisions)
                )
                {
                    _logger?.LogDebug(
                        WatchReloadEvent,
                        "Configuration changed for {ModelType} options {OptionsName}; observed sources {SourceIds}.",
                        typeof(TModel).FullName,
                        _optionsName,
                        current.Revisions is { } revisions
                            ? string.Join(",", revisions.Revisions.Keys)
                            : string.Empty
                    );
                    NotifyListeners(current.Value!);
                }
                else if (current.Status != StateReadStatus.Success)
                {
                    NotifyReloadFailed(
                        new InvalidOperationException(
                            $"Configuration reload resolved to state status '{current.Status}'."
                        )
                    );
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                        .ConfigureAwait(false);
                }

                previous = current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var watcherSources = GetActiveSources()
                    .Where(static source => source.Watcher is not null)
                    .ToArray();
                _logger?.LogError(
                    WatchFailureEvent,
                    exception,
                    "Watching configuration changes failed for {ModelType} options {OptionsName}; sources {SourceIds}, resources {ResourceIds}.",
                    typeof(TModel).FullName,
                    _optionsName,
                    string.Join(",", watcherSources.Select(static source => source.Id)),
                    string.Join(
                        ",",
                        watcherSources
                            .Where(static source => source.ResourceId is not null)
                            .Select(static source => source.ResourceId!.Value.Value)
                    )
                );
                NotifyReloadFailed(exception);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task WaitForAnyChangeAsync(
        StateRevisionVector? revisions,
        CancellationToken cancellationToken
    )
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var waitTasks = new List<Task>();
        StateSource<TFragment>[] activeSources;
        Task topologyChanged;
        lock (_sourceGate)
        {
            activeSources = _activeSources;
            topologyChanged = _sourceTopologyChanged.Task;
        }

        if (
            revisions is null
            || revisions.Revisions.Keys.Any(revisionSourceId =>
                !activeSources.Any(source =>
                    string.Equals(source.Id, revisionSourceId, StringComparison.Ordinal)
                )
            )
        )
        {
            return;
        }

        try
        {
            foreach (var source in activeSources)
            {
                if (
                    source.Watcher is not null
                    && revisions.TryGetRevision(source.Id, out var revision)
                )
                {
                    waitTasks.Add(
                        source.Watcher.WaitForChangeAsync(revision, waitCancellation.Token).AsTask()
                    );
                }
            }

            waitTasks.Add(topologyChanged.WaitAsync(waitCancellation.Token));
            var completed = await Task.WhenAny(waitTasks).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            await waitCancellation.CancelAsync().ConfigureAwait(false);
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
                listener(CloneModel(value));
            }
            catch (Exception exception)
            {
                _logger?.LogError(
                    ListenerFailureEvent,
                    exception,
                    "A configuration change listener failed for {ModelType} options {OptionsName}.",
                    typeof(TModel).FullName,
                    _optionsName
                );
            }
        }
    }

    private void NotifyReloadFailed(Exception exception)
    {
        Action<Exception>[] listeners;
        lock (_changeGate)
        {
            if (_disposed)
            {
                return;
            }

            listeners = _reloadFailureListeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            try
            {
                listener(exception);
            }
            catch (Exception listenerException)
            {
                _logger?.LogError(
                    ReloadFailureListenerEvent,
                    listenerException,
                    "A reload-failure listener failed for {ModelType} options {OptionsName}.",
                    typeof(TModel).FullName,
                    _optionsName
                );
            }
        }
    }

    private void EnsureWatcherStarted()
    {
        if (_watchTask is null || _watchTask.IsCompleted)
        {
            _watchCancellation?.Dispose();
            _watchCancellation = new CancellationTokenSource();
            _watchTask = WatchChangesAsync(_watchCancellation.Token);
        }
    }

    private TModel CloneModel(TModel value)
    {
        var clone = _cloneStrategy is null ? value.DeepClone() : _cloneStrategy(value);
        if (clone is null || ReferenceEquals(clone, value))
        {
            throw new InvalidOperationException(
                "The model clone strategy must return a non-null, distinct model instance."
            );
        }

        return clone;
    }

    private void UpdateCurrentValueCache(TModel value)
    {
        lock (_currentValueGate)
        {
            Volatile.Write(ref _currentValueCache, new CurrentValueCacheEntry(value));
        }
    }

    private void InvalidateCurrentValueCache() => Volatile.Write(ref _currentValueCache, null);

    TModel IConfiglueValueCloneProvider<TModel>.CloneValue(TModel value) => CloneModel(value);

    private static object? GetModelValue(
        ConfiglueModelSchema schema,
        object model,
        IReadOnlyList<string> path,
        string propertyPath,
        out ConfiglueMemberSchema leafMember
    )
    {
        leafMember = default;
        object? current = model;
        for (var index = 0; index < path.Count; index++)
        {
            var member = schema.Members.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, path[index], StringComparison.Ordinal)
            );
            if (string.IsNullOrEmpty(member.Name))
            {
                throw new ArgumentException(
                    $"Model '{schema.ModelType}' has no member named '{path[index]}' in property path '{propertyPath}'.",
                    nameof(propertyPath)
                );
            }

            object? value = null;
            if (current is not null)
            {
                var getter =
                    member.GetValue
                    ?? throw new InvalidOperationException(
                        $"Generated getter metadata is missing for '{schema.ModelType}.{member.Name}'."
                    );
                value = getter(current);
            }

            if (index == path.Count - 1)
            {
                leafMember = member;
                return value;
            }

            if (member.NestedSchemaFactory is null)
            {
                throw new ArgumentException(
                    $"Member '{schema.ModelType}.{member.Name}' is not a generated nested model.",
                    nameof(propertyPath)
                );
            }

            schema = member.NestedSchemaFactory();
            current = value;
        }

        throw new ArgumentException("The property path is empty.", nameof(propertyPath));
    }

    private static bool IsGeneratedCollectionType(Type valueType)
    {
        if (valueType.IsArray)
        {
            return true;
        }

        if (!valueType.IsGenericType)
        {
            return false;
        }

        var definition = valueType.GetGenericTypeDefinition();
        return definition == typeof(IEnumerable<>)
            || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(IReadOnlyList<>)
            || definition == typeof(List<>)
            || definition == typeof(HashSet<>)
            || definition == typeof(ISet<>)
            || definition == typeof(IReadOnlySet<>);
    }

    private static bool IsCollectionType(ConfiglueMemberSchema member) =>
        member.MergeStrategy is not null
            ? member.ValueType != typeof(string)
                && typeof(IEnumerable).IsAssignableFrom(member.ValueType)
            : IsGeneratedCollectionType(member.ValueType);

    private static IReadOnlyList<ConfiglueCollectionElementExplanation> ExplainCollectionElements(
        ConfiglueMemberSchema member,
        object? effectiveValue,
        IReadOnlyList<ConfiglueSourceContribution> sourceContributions
    )
    {
        var effectiveElements = GetCollectionElements(effectiveValue);
        var elementContributions = Enumerable
            .Range(0, effectiveElements.Length)
            .Select(static _ => new List<ConfiglueSourceContribution>())
            .ToArray();

        if (member.MergeStrategy is { } mergeStrategy)
        {
            var sourcePriority = sourceContributions
                .Select((source, index) => (source.SourceId, index))
                .ToDictionary(
                    static source => source.SourceId,
                    static source => source.index,
                    StringComparer.Ordinal
                );
            var sourceValues = sourceContributions
                .Reverse()
                .Select(static source => new ConfiglueMergeSourceValue(
                    source.SourceId,
                    Optional<object?>.Present(source.Value)
                ))
                .ToArray();
            foreach (var provenance in mergeStrategy.ExplainElements(effectiveValue, sourceValues))
            {
                if (provenance.Index >= effectiveElements.Length)
                {
                    throw new InvalidOperationException(
                        $"Merge strategy for '{member.Name}' returned provenance for out-of-range element index {provenance.Index}."
                    );
                }

                var orderedSourceIndices = new SortedSet<int>();
                foreach (var sourceId in provenance.SourceIds)
                {
                    if (!sourcePriority.TryGetValue(sourceId, out var sourceIndex))
                    {
                        throw new InvalidOperationException(
                            $"Merge strategy for '{member.Name}' referenced unknown source '{sourceId}' in element provenance."
                        );
                    }

                    orderedSourceIndices.Add(sourceIndex);
                }

                foreach (var sourceIndex in orderedSourceIndices)
                {
                    var source = sourceContributions[sourceIndex];
                    elementContributions[provenance.Index]
                        .Add(
                            new ConfiglueSourceContribution(
                                source.SourceId,
                                source.PhysicalOrigin,
                                source.Revision,
                                effectiveElements[provenance.Index]
                            )
                        );
                }
            }

            return Array.AsReadOnly(
                effectiveElements
                    .Select(
                        (value, index) =>
                            new ConfiglueCollectionElementExplanation(
                                index,
                                value,
                                elementContributions[index]
                            )
                    )
                    .ToArray()
            );
        }

        if (member.MergeMode == MergeMode.Append && effectiveValue is IList)
        {
            var expectedElementCount = sourceContributions.Sum(contribution =>
                GetCollectionElements(contribution.Value).Length
            );
            if (expectedElementCount == effectiveElements.Length)
            {
                var elementIndex = 0;
                foreach (var contribution in sourceContributions.Reverse())
                {
                    foreach (var _ in GetCollectionElements(contribution.Value))
                    {
                        AddElementContribution(
                            elementContributions,
                            elementIndex,
                            effectiveElements[elementIndex],
                            contribution
                        );
                        elementIndex++;
                    }
                }
            }
            else
            {
                AddMatchingElementContributions(
                    effectiveElements,
                    sourceContributions,
                    elementContributions
                );
            }
        }
        else
        {
            var eligibleSources =
                member.MergeMode == MergeMode.Replace
                    ? sourceContributions.Take(1)
                    : sourceContributions;
            AddMatchingElementContributions(
                effectiveElements,
                eligibleSources,
                elementContributions
            );
        }

        return Array.AsReadOnly(
            effectiveElements
                .Select(
                    (value, index) =>
                        new ConfiglueCollectionElementExplanation(
                            index,
                            value,
                            elementContributions[index]
                        )
                )
                .ToArray()
        );
    }

    private static object?[] GetCollectionElements(object? value) =>
        value is IEnumerable elements && value is not string
            ? elements.Cast<object?>().ToArray()
            : [];

    private static void AddMatchingElementContributions(
        IReadOnlyList<object?> effectiveElements,
        IEnumerable<ConfiglueSourceContribution> sourceContributions,
        IReadOnlyList<List<ConfiglueSourceContribution>> elementContributions
    )
    {
        foreach (var (element, index) in effectiveElements.Select((value, index) => (value, index)))
        {
            foreach (
                var sourceContribution in sourceContributions.Where(source =>
                    GetCollectionElements(source.Value)
                        .Any(sourceElement => Equals(sourceElement, element))
                )
            )
            {
                AddElementContribution(elementContributions, index, element, sourceContribution);
            }
        }
    }

    private static void AddElementContribution(
        IReadOnlyList<List<ConfiglueSourceContribution>> elementContributions,
        int elementIndex,
        object? element,
        ConfiglueSourceContribution source
    ) =>
        elementContributions[elementIndex]
            .Add(
                new ConfiglueSourceContribution(
                    source.SourceId,
                    source.PhysicalOrigin,
                    source.Revision,
                    element
                )
            );

    private static bool TryGetFragmentValue(
        IConfiglueFragment fragment,
        IReadOnlyList<string> path,
        out object? value
    )
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

        return left.Revisions.All(pair =>
                right.TryGetRevision(pair.Key, out var revision)
                && string.Equals(pair.Value, revision, StringComparison.Ordinal)
            )
            && left.NestedRevisions.Count == right.NestedRevisions.Count
            && left.NestedRevisions.All(pair =>
                right.TryGetNestedRevisions(pair.Key, out var nested)
                && HaveSameRevisions(pair.Value, nested)
            );
    }

    private static TaskCompletionSource NewTopologySignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RemoveChangeListener(Action<TModel> listener)
    {
        lock (_changeGate)
        {
            _changeListeners.Remove(listener);
        }
    }

    private void RemoveReloadFailureListener(Action<Exception> listener)
    {
        lock (_changeGate)
        {
            _reloadFailureListeners.Remove(listener);
        }
    }

    private sealed class FragmentChangesPatch(TFragment changes) : IConfiglueMemberPatch
    {
        public TFragment Changes => changes;

        public ConfiglueModelSchema Schema => TModel.ConfiglueSchema;

        public bool IsEmpty => changes.IsEmpty;

        public IConfiglueFragment Apply(IConfiglueFragment fragment)
        {
            if (fragment is not TFragment sourceFragment)
            {
                throw new ArgumentException(
                    "The patch received an incompatible fragment.",
                    nameof(fragment)
                );
            }

            return sourceFragment.ApplyChanges(changes);
        }

        public IConfigluePatch SelectMembers(ReadOnlySpan<int> memberIds)
        {
            var selected = TFragment.Empty;
            foreach (var member in changes.EnumeratePresentMembers())
            {
                for (var index = 0; index < memberIds.Length; index++)
                {
                    if (memberIds[index] == member.Id)
                    {
                        selected = (TFragment)selected.WithMember(member.Id, member.Value);
                        break;
                    }
                }
            }

            return new FragmentChangesPatch(selected);
        }
    }

    private sealed record ResolvedContribution(
        StateSource<TFragment> Source,
        StateReadResult<TFragment> Result
    );

    private sealed record ResolvedState(
        StateReadResult<TModel> Result,
        IReadOnlyList<ResolvedContribution> Contributions,
        TFragment? MergedFragment
    );

    private sealed class CurrentValueCacheEntry
    {
        public CurrentValueCacheEntry(TModel value)
        {
            Value = value;
        }

        public TModel Value { get; }
    }

    private sealed class ChangeSubscription(
        ConfiglueOptions<TModel, TFragment> owner,
        Action<TModel> listener
    ) : IDisposable
    {
        private ConfiglueOptions<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveChangeListener(listener);
    }

    private sealed class ReloadFailureSubscription(
        ConfiglueOptions<TModel, TFragment> owner,
        Action<Exception> listener
    ) : IDisposable
    {
        private ConfiglueOptions<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveReloadFailureListener(listener);
    }

    private static bool CanFallBack(StateFallbackCondition condition, StateReadStatus status) =>
        status switch
        {
            StateReadStatus.NotFound => (condition & StateFallbackCondition.NotFound) != 0,
            StateReadStatus.Unavailable => (condition & StateFallbackCondition.Unavailable) != 0,
            _ => false,
        };
}
