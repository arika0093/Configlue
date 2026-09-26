using System.Collections;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Configlue;

/// <summary>Resolves and saves a generated configuration model over a set of state sources.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
public sealed class ConfiglueOptions<TModel, TFragment>
    : IWritableOptions<TModel>,
        IDisposable,
        IAsyncDisposable
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly StateSourceSet<TFragment> _sourceSet;
    private readonly object _sourceGate = new();
    private readonly HashSet<string> _retiredSourceIds = new(StringComparer.Ordinal);
    private StateSource<TFragment>[] _activeSources;
    private TaskCompletionSource _sourceTopologyChanged = NewTopologySignal();
    private readonly StateWriteRoute _writeRoute;
    private readonly StateSchemaMigrationChain<TFragment> _migrationChain;
    private readonly IConfiglueValidator<TModel>[] _validators;
    private readonly string _optionsName;
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
        TimeSpan? onChangeDebounce = null,
        string? optionsName = null
    )
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        _sourceSet = sourceSet;
        _activeSources = sourceSet.Sources.ToArray();
        _writeRoute = writeRoute;
        _validators = validators?.ToArray() ?? [];
        _optionsName = optionsName ?? Options.DefaultName;
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
    public ValueTask<StateReadResult<TModel>> ReadAsync(
        CancellationToken cancellationToken = default
    ) => ReadCoreAsync(null, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ConfiglueValueExplanation> ExplainAsync(
        string propertyPath,
        CancellationToken cancellationToken = default
    )
    {
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

        var collectionElements = IsGeneratedCollectionType(member.ValueType)
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
                sourceResult = await source
                    .Reader.ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = sourceResult.FromSource(source.Id, source.PhysicalOrigin);
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
                    fragment = await MigrateAsync(fragment, sourceSchema, cancellationToken)
                        .ConfigureAwait(false);
                }

                contributions.Add(
                    new ResolvedContribution(source, result with { Value = fragment })
                );
                continue;
            }

            lastFailure = result;
            if (!CanFallBack(source.FallbackCondition, result.Status))
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
            TModel.ConfiglueSchema.ToMetadata()
        ) with
        {
            SourceId = active?.Source.Id,
            PhysicalOrigin = active?.Result.PhysicalOrigin,
            Revisions = new StateRevisionVector(revisions, nestedRevisions),
        };
        return new ResolvedState(resolvedResult, contributions, merged);
    }

    /// <inheritdoc />
    public ValueTask<ConfigureSession<TModel>> BeginConfigureAsync(
        CancellationToken cancellationToken = default
    ) => BeginConfigureCoreAsync(null, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ConfigureSession<TModel>> BeginConfigureAsync(
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        return BeginConfigureCoreAsync(writePlan, cancellationToken);
    }

    private async ValueTask<ConfigureSession<TModel>> BeginConfigureCoreAsync(
        StateWritePlan? writePlan,
        CancellationToken cancellationToken
    )
    {
        var resolvedState = await ResolveCoreAsync(null, cancellationToken).ConfigureAwait(false);
        var resolved = resolvedState.Result;
        if (resolved.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {resolved.Status}."
            );
        }

        var source = SelectWriteSource();
        if (writePlan is not null)
        {
            ValidateWritePlan(writePlan);
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

        var draft = resolved.Value!.DeepClone();
        var baseline = resolved.Value.DeepClone();
        var expectedRevisions = resolved.Revisions;
        return new ConfigureSession<TModel>(
            draft,
            async (value, token) =>
            {
                var latest = await ReadAsync(token).ConfigureAwait(false);
                if (
                    latest.Status != StateReadStatus.Success
                    || !HaveSameRevisions(expectedRevisions, latest.Revisions)
                )
                {
                    throw new StateConflictException(
                        "A state source changed after the configuration edit began."
                    );
                }

                if (writePlan is null || writePlan.PropertyRoutes.Count == 0)
                {
                    return await WriteChangesToSourceAsync(
                            source,
                            baseline,
                            value,
                            expectedRevision,
                            resolvedState.Contributions,
                            token
                        )
                        .ConfigureAwait(false);
                }

                return await WriteChangesToSourcesAsync(
                        source,
                        baseline,
                        value,
                        expectedRevision,
                        expectedRevisions,
                        resolvedState.Contributions,
                        writePlan,
                        token
                    )
                    .ConfigureAwait(false);
            }
        );
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(
        TModel value,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = SelectWriteSource();
        var current = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException(
                $"Cannot safely write configuration because source '{source.Id}' is unavailable."
            );
        }

        return await WriteToSourceAsync(source, value, current.Revision, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(
        TModel value,
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(writePlan);
        using var session = await BeginConfigureAsync(writePlan, cancellationToken)
            .ConfigureAwait(false);
        session.Value = value;
        return await session.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(
        Action<TModel> update,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(update);
        using var session = await BeginConfigureAsync(cancellationToken).ConfigureAwait(false);
        var value = session.Value;
        update(value);
        session.Value = value;
        return await session.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(
        Action<TModel> update,
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(writePlan);
        using var session = await BeginConfigureAsync(writePlan, cancellationToken)
            .ConfigureAwait(false);
        var value = session.Value;
        update(value);
        session.Value = value;
        return await session.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(
        Func<TModel, Task> update,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(update);
        using var session = await BeginConfigureAsync(cancellationToken).ConfigureAwait(false);
        var value = session.Value;
        await update(value).ConfigureAwait(false);
        session.Value = value;
        return await session.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(
        Func<TModel, Task> update,
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(writePlan);
        using var session = await BeginConfigureAsync(writePlan, cancellationToken)
            .ConfigureAwait(false);
        var value = session.Value;
        await update(value).ConfigureAwait(false);
        session.Value = value;
        return await session.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> ApplyPatchAsync(
        IConfigluePatch patch,
        CancellationToken cancellationToken = default
    )
    {
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
        var current = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException(
                $"Cannot safely patch configuration because source '{source.Id}' is unavailable."
            );
        }

        if (patch.IsEmpty)
        {
            return new StateWriteResult(current.Revision);
        }

        var sourceFragment =
            current.Status == StateReadStatus.Success
                ? current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{source.Id}' returned a null configuration fragment."
                    )
                : TFragment.Empty;
        if (current.Schema is { } schema)
        {
            sourceFragment = await MigrateAsync(sourceFragment, schema, cancellationToken)
                .ConfigureAwait(false);
        }

        if (patch.Apply(sourceFragment) is not TFragment patchedFragment)
        {
            throw new InvalidOperationException(
                "The patch returned an incompatible configuration fragment."
            );
        }

        var proposedResult = StateReadResult<TFragment>.Success(
            patchedFragment,
            current.Revision,
            modelSchema.ToMetadata()
        );
        var proposed = await ReadCoreAsync(
                new Dictionary<string, StateReadResult<TFragment>>(StringComparer.Ordinal)
                {
                    [source.Id] = proposedResult,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        if (proposed.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"The patched configuration could not be resolved: {proposed.Status}."
            );
        }

        Validate(proposed.Value!);
        if (!IsSourceActive(source.Id))
        {
            throw new StateConflictException(
                $"State source '{source.Id}' was retired while the patch was being prepared."
            );
        }

        return await source
            .Writer!.WriteAsync(
                new StateWriteRequest<TFragment>(
                    patchedFragment,
                    current.Revision,
                    CheckRevision: true
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
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
            throw new StateConflictException(
                "A state source changed after the configuration edit began."
            );
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
                throw new StateConflictException(
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
            if (
                source.Writer is IStateWriteBatchParticipant<TFragment> participant
                && participant.TryCreateBatchWrite(
                    request,
                    out var participantResourceId,
                    out batchWriter,
                    out mutation
                )
            )
            {
                if (
                    sourceResourceId is { } declaredResourceId
                    && declaredResourceId != participantResourceId
                )
                {
                    throw new InvalidOperationException(
                        $"State source '{source.Id}' declares resource '{declaredResourceId}' but its writer targets '{participantResourceId}'."
                    );
                }

                if (
                    batchWriter is IResourceIdentity batchIdentity
                    && batchIdentity.ResourceId != participantResourceId
                )
                {
                    throw new InvalidOperationException(
                        $"State source '{source.Id}' prepares a mutation for '{participantResourceId}' but its batch writer targets '{batchIdentity.ResourceId}'."
                    );
                }

                sourceResourceId = participantResourceId;
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
                && !TModel.Diff(proposed.Result.Value!, expectedModel).IsEmpty
            )
            {
                throw new StateConflictException(
                    "The configured source routes cannot realize the requested edit."
                );
            }

            if (!HaveSameRevisions(baseline.Result.Revisions, proposed.Result.Revisions))
            {
                throw new StateConflictException(
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
                throw new StateConflictException(
                    "A state source changed before the patch batch could be written."
                );
            }
        }

        var results = new Dictionary<string, StateSourceWriteResult>(
            noOpResults,
            StringComparer.Ordinal
        );
        var physicalWriteCount = 0;
        foreach (var group in writeGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Count == 1)
            {
                var plan = group[0];
                var write = await plan
                    .Writer.WriteAsync(plan.Request, cancellationToken)
                    .ConfigureAwait(false);
                results.Add(
                    plan.Source.Id,
                    new StateSourceWriteResult(plan.Source.Id, plan.ResourceId, write.Revision)
                );
                physicalWriteCount++;
                continue;
            }

            var batchWriter = group[0].BatchWriter!;
            var batchResult = await batchWriter
                .WriteBatchAsync(
                    group.Select(static plan => plan.Mutation!).ToArray(),
                    cancellationToken
                )
                .ConfigureAwait(false);
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
        }

        return new StateMultiWriteResult(
            patchRequests.Select(patch => results[patch.SourceId]),
            physicalWriteCount
        );
    }

    /// <inheritdoc />
    public async ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        string sourceId,
        string targetId,
        CancellationToken cancellationToken = default
    )
    {
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

        var sourceResult = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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
            : await target.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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

        var write = await target
            .Writer.WriteAsync(
                new StateWriteRequest<TFragment>(
                    sourceFragment,
                    targetResult.Revision,
                    CheckRevision: true
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        return new StateSourceMigrationResult(
            source.Id,
            target.Id,
            sourceResult.Revision,
            write.Revision
        );
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
        foreach (var source in _sourceSet.Sources.Where(source => selectedIds.Contains(source.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = (
                await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ).FromSource(source.Id, source.PhysicalOrigin);
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
                var latest = await contribution
                    .Source.Reader.ReadAsync(cancellationToken)
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
                    throw new StateConflictException(
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
                    _ => throw new StateConflictException(
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
                    throw new StateConflictException(
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
            var current = (
                await target.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ).FromSource(target.Id, target.PhysicalOrigin);
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
                var confirmation = (
                    await target.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ).FromSource(target.Id, target.PhysicalOrigin);
                if (
                    confirmation.Status != current.Status
                    || !string.Equals(
                        confirmation.Revision,
                        current.Revision,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new StateConflictException(
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
                        throw new StateConflictException(
                            $"Target source '{target.Id}' changed during migration verification."
                        );
                    }
                }

                await VerifySourceSnapshotsAsync().ConfigureAwait(false);
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
            var write = await writer
                .WriteAsync(
                    new StateWriteRequest<TFragment>(
                        desired,
                        current.Revision,
                        CheckRevision: true
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            var verification = (
                await target.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ).FromSource(target.Id, target.PhysicalOrigin);
            if (
                verification.Status != StateReadStatus.Success
                || !string.Equals(verification.Revision, write.Revision, StringComparison.Ordinal)
            )
            {
                throw new StateConflictException(
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
                throw new StateConflictException(
                    $"Target source '{target.Id}' did not retain the migrated fragment."
                );
            }

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
        }

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
                throw new StateConflictException(
                    $"Target source '{target.Id}' changed before source retirement."
                );
            }

            var currentFragment = current.Status switch
            {
                StateReadStatus.NotFound when desired.IsEmpty => TFragment.Empty,
                StateReadStatus.Success => current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{target.Id}' returned a null configuration fragment."
                    ),
                _ => throw new StateConflictException(
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
                throw new StateConflictException(
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
            throw new StateConflictException(
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
                throw new StateConflictException(
                    $"Target source '{target.Id}' changed while source retirement was being verified."
                );
            }

            if (latest.Status == StateReadStatus.NotFound && desired.IsEmpty)
            {
                continue;
            }

            if (latest.Status != StateReadStatus.Success || latest.Value is null)
            {
                throw new StateConflictException(
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
                throw new StateConflictException(
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
        Validate(after);
        var changes = TModel.Diff(before, after);
        if (changes.IsEmpty)
        {
            return new StateWriteResult(expectedFallbackRevision);
        }

        var routedChanges = PartitionRoutedChanges(
            TModel.ConfiglueSchema,
            changes,
            after,
            [],
            fallbackSource.Id,
            writePlan
        );
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

        if (patches.Count == 0)
        {
            return new StateWriteResult(expectedFallbackRevision);
        }

        var result = await ApplyPatchesCoreAsync(
                patches.ToArray(),
                expectedBaselineRevisions,
                after,
                cancellationToken
            )
            .ConfigureAwait(false);
        var fallbackResult = result.Sources.FirstOrDefault(source =>
            string.Equals(source.SourceId, fallbackSource.Id, StringComparison.Ordinal)
        );
        var revision = fallbackResult.SourceId is not null
            ? fallbackResult.Revision
            : result.Sources[0].Revision;
        return new StateWriteResult(revision) { MultiWriteResult = result };
    }

    private static Dictionary<string, IConfiglueFragment> PartitionRoutedChanges(
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
                    throw new StateConflictException(
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

    private ValueTask<StateWriteResult> WriteToSourceAsync(
        StateSource<TFragment> source,
        TModel value,
        string? expectedRevision,
        CancellationToken cancellationToken
    )
    {
        if (!IsSourceActive(source.Id))
        {
            throw new StateConflictException(
                $"State source '{source.Id}' was retired before the write began."
            );
        }

        Validate(value);
        return source.Writer!.WriteAsync(
            new StateWriteRequest<TFragment>(
                TModel.ToFragment(value),
                expectedRevision,
                CheckRevision: true
            ),
            cancellationToken
        );
    }

    private async ValueTask<StateWriteResult> WriteChangesToSourceAsync(
        StateSource<TFragment> source,
        TModel before,
        TModel after,
        string? expectedRevision,
        IReadOnlyList<ResolvedContribution> baselineContributions,
        CancellationToken cancellationToken
    )
    {
        Validate(after);
        var changes = TModel.Diff(before, after);
        if (changes.IsEmpty)
        {
            return new StateWriteResult(expectedRevision);
        }

        var current = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
        {
            throw new StateConflictException(
                $"State source '{source.Id}' changed after the configuration edit began."
            );
        }

        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException(
                $"Cannot safely update configuration because source '{source.Id}' is unavailable."
            );
        }

        var sourceFragment =
            current.Status == StateReadStatus.Success
                ? current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{source.Id}' returned a null configuration fragment."
                    )
                : TFragment.Empty;
        if (current.Schema is { } schema)
        {
            sourceFragment = await MigrateAsync(sourceFragment, schema, cancellationToken)
                .ConfigureAwait(false);
        }

        changes = (TFragment)PlanMergeAwareChanges(
            TModel.ConfiglueSchema,
            changes,
            after,
            [],
            source.Id,
            baselineContributions
        );
        var updated = sourceFragment.ApplyChanges(changes);
        var proposed = await ReadCoreAsync(
                new Dictionary<string, StateReadResult<TFragment>>(StringComparer.Ordinal)
                {
                    [source.Id] = StateReadResult<TFragment>.Success(
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
            throw new InvalidOperationException(
                $"The edited configuration could not be resolved: {proposed.Status}."
            );
        }

        if (!TModel.Diff(proposed.Value!, after).IsEmpty)
        {
            throw new StateConflictException(
                $"State source '{source.Id}' cannot realize the requested edit while preserving higher-priority contributions."
            );
        }

        Validate(proposed.Value!);
        return await source
            .Writer!.WriteAsync(
                new StateWriteRequest<TFragment>(updated, current.Revision, CheckRevision: true),
                cancellationToken
            )
            .ConfigureAwait(false);
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

    private static List<object?> PlanAppendContribution(
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
            throw new StateConflictException(
                $"The edit to append-merged member '{memberName}' cannot be represented by source '{sourceOrder[targetIndex].Id}' while preserving other source contributions."
            );
        }

        return desired
            .Skip(prefix.Count)
            .Take(desired.Count - prefix.Count - suffix.Count)
            .ToList();
    }

    private static List<object?> PlanSetUnionContribution(
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
            throw new StateConflictException(
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
            throw new StateConflictException(
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

        if (_validateDataAnnotations)
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
            throw new OptionsValidationException(_optionsName, typeof(TModel), failures);
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
                    NotifyListeners(current.Value!);
                }
                else if (current.Status != StateReadStatus.Success)
                {
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
                System.Diagnostics.Trace.TraceError(
                    "Configlue failed while watching configuration changes: {0}",
                    exception
                );
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
                listener(value.DeepClone());
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "Configlue change listener failed: {0}",
                    exception
                );
            }
        }
    }

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

    private sealed class FragmentChangesPatch(TFragment changes) : IConfigluePatch
    {
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

    private sealed class ChangeSubscription(
        ConfiglueOptions<TModel, TFragment> owner,
        Action<TModel> listener
    ) : IDisposable
    {
        private ConfiglueOptions<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveChangeListener(listener);
    }

    private static bool CanFallBack(StateFallbackCondition condition, StateReadStatus status) =>
        status switch
        {
            StateReadStatus.NotFound => (condition & StateFallbackCondition.NotFound) != 0,
            StateReadStatus.Unavailable => (condition & StateFallbackCondition.Unavailable) != 0,
            _ => false,
        };
}
