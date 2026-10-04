using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Owns write coordination for one runtime: write-plan ownership, patch routing,
/// merge-aware change planning, atomic multi-source application, and write previews.
///
/// Holds the bound <see cref="StateWritePlan"/> and the default-source inference
/// result. Reads resolve through the resolution engine; validation runs through
/// the validation pipeline. Physical grouping state (<c>PendingSourceWrite</c>)
/// never escapes this coordinator.
/// </summary>
internal sealed class RuntimeWriteCoordinator<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeSourceTopology<TFragment> _topology;
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeValidationPipeline<TModel, TFragment> _validation;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeLifetime _lifetime;
    private readonly RuntimeSubjectContext _subjects;
    private readonly RuntimeModelCloner<TModel, TFragment> _cloner;
    private readonly string _stateName;
    private readonly StateWritePlan _writePlan;
    private readonly bool _defaultWriteSourceIsInferred;

    internal RuntimeWriteCoordinator(
        RuntimeSourceTopology<TFragment> topology,
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeValidationPipeline<TModel, TFragment> validation,
        RuntimeDiagnosticRecorder diagnostics,
        RuntimeLifetime lifetime,
        RuntimeSubjectContext subjects,
        RuntimeModelCloner<TModel, TFragment> cloner,
        string stateName,
        StateWritePlan configuredWritePlan
    )
    {
        _topology = topology;
        _engine = engine;
        _validation = validation;
        _diagnostics = diagnostics;
        _lifetime = lifetime;
        _subjects = subjects;
        _cloner = cloner;
        _stateName = stateName;
        (_writePlan, _defaultWriteSourceIsInferred) = ResolveWriteOwnership(
            topology.GetActiveSources(),
            configuredWritePlan ?? StateWritePlan.Empty
        );
    }

    internal StateWritePlan Plan => _writePlan;

    internal bool DefaultWriteSourceIsInferred => _defaultWriteSourceIsInferred;

    private static (StateWritePlan Plan, bool DefaultInferred) ResolveWriteOwnership(
        IReadOnlyList<StateSource<TFragment>> sources,
        StateWritePlan configuredWritePlan
    )
    {
        var ownedPaths = new List<(string Path, SourceId SourceId)>();
        foreach (var source in sources)
        {
            if (source.Writer is null || source.ExplicitOnly)
            {
                continue;
            }

            foreach (var path in source.OwnedPropertyPaths)
            {
                ownedPaths.Add((path, source.Id));
            }
        }

        if (ownedPaths.Count > 1 && HasOverlappingOwnershipPaths(ownedPaths))
        {
            ThrowOverlappingOwnership(ownedPaths);
        }

        var owners = new Dictionary<string, SourceId>(StringComparer.Ordinal);
        foreach (var (path, sourceId) in ownedPaths)
        {
            owners.Add(path, sourceId);
        }

        var mountedWritePlan =
            owners.Count == 0 ? StateWritePlan.Empty : new StateWritePlan(null, owners);
        var merged = mountedWritePlan.OverrideWith(configuredWritePlan);

        var defaultSourceId = configuredWritePlan.DefaultSourceId;
        var defaultInferred = false;
        if (defaultSourceId is null)
        {
            StateSource<TFragment>? singleRoot = null;
            foreach (var source in sources)
            {
                if (
                    source.Writer is null
                    || source.ExplicitOnly
                    || source.OwnedPropertyPaths.Count > 0
                )
                {
                    continue;
                }

                if (singleRoot is not null)
                {
                    throw new InvalidOperationException(
                        $"Model '{typeof(TModel)}' has multiple writable root sources ('{singleRoot.Id}' and '{source.Id}') and no default write owner. "
                            + "Configure a default write owner with model.Writes(write => write.DefaultTo(...))."
                    );
                }

                singleRoot = source;
            }

            if (singleRoot is not null)
            {
                defaultSourceId = singleRoot.Id;
                defaultInferred = true;
            }
        }
        else if (!sources.Any(source => source.Id == defaultSourceId))
        {
            throw new InvalidOperationException(
                $"The configured default write source '{defaultSourceId}' is not registered for model '{typeof(TModel)}'."
            );
        }

        return (
            merged
                .WithDefaultSourceId(defaultSourceId)
                .Bind(RuntimeModel<TModel, TFragment>.Schema),
            defaultInferred
        );

        static bool HasOverlappingOwnershipPaths(List<(string Path, SourceId SourceId)> paths)
        {
            var sorted = new string[paths.Count];
            for (var index = 0; index < paths.Count; index++)
            {
                sorted[index] = paths[index].Path;
            }

            Array.Sort(sorted, static (first, second) => CompareOwnedPathOrder(first, second));
            for (var index = 1; index < sorted.Length; index++)
            {
                if (PathsOverlap(sorted[index - 1], sorted[index]))
                {
                    return true;
                }
            }

            return false;
        }

        static int CompareOwnedPathOrder(string first, string second)
        {
            var length = Math.Min(first.Length, second.Length);
            for (var index = 0; index < length; index++)
            {
                var comparison = OwnedPathCharOrder(first[index])
                    .CompareTo(OwnedPathCharOrder(second[index]));
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return first.Length.CompareTo(second.Length);
        }

        static int OwnedPathCharOrder(char value) => value == '.' ? 0 : value + 1;

        static bool PathsOverlap(string first, string second) =>
            string.Equals(first, second, StringComparison.Ordinal)
            || IsAncestorOwnedPath(first, second)
            || IsAncestorOwnedPath(second, first);

        static bool IsAncestorOwnedPath(string ancestor, string descendant) =>
            descendant.Length > ancestor.Length
            && descendant[ancestor.Length] == '.'
            && descendant.AsSpan(0, ancestor.Length).SequenceEqual(ancestor.AsSpan());

        static void ThrowOverlappingOwnership(List<(string Path, SourceId SourceId)> paths)
        {
            var seenOwners = new Dictionary<string, SourceId>(StringComparer.Ordinal);
            foreach (var (path, sourceId) in paths)
            {
                var existingOwner = seenOwners.FirstOrDefault(owner =>
                    string.Equals(owner.Key, path, StringComparison.Ordinal)
                    || owner.Key.StartsWith(path + ".", StringComparison.Ordinal)
                    || path.StartsWith(owner.Key + ".", StringComparison.Ordinal)
                );
                if (!string.IsNullOrEmpty(existingOwner.Key))
                {
                    throw new InvalidOperationException(
                        $"Writable sources '{existingOwner.Value}' and '{sourceId}' have overlapping ownership paths '{existingOwner.Key}' and '{path}'. Configure one source as explicit-only."
                    );
                }

                seenOwners.Add(path, sourceId);
            }
        }
    }

    internal void ValidateWritePlan(StateWritePlan writePlan)
    {
        foreach (var sourceId in writePlan.PropertyRoutes.Values)
        {
            var source = _topology.FindSource(sourceId);
            if (!_topology.IsSourceActive(source.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' has been retired from this state instance."
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

    private StateSource<TFragment>? ResolveDefaultWriteSource()
    {
        if (
            _topology.IsSingleSourceFastPath
            && _topology.FastPathWriteSource is not null
            && ReferenceEquals(_topology.GetActiveSources(), _topology.FastPathSources)
        )
        {
            // Single-file fast path (#231): the only writable root was pre-resolved at
            // construction, so ordinary saves skip the per-save topology scan.
            return _topology.FastPathWriteSource;
        }

        if (_writePlan.DefaultSourceId is not { } defaultSourceId)
        {
            return null;
        }

        var source = _topology
            .GetActiveSources()
            .FirstOrDefault(candidate => candidate.Id == defaultSourceId);
        if (source is null)
        {
            if (_topology.SourceSet.Sources.Any(candidate => candidate.Id == defaultSourceId))
            {
                throw new InvalidOperationException(
                    $"State source '{defaultSourceId}' has been retired from this state instance."
                );
            }

            throw new InvalidOperationException(
                $"State source '{defaultSourceId}' is not registered."
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

    private ConfiglueResourceContext GetResourceContext(StateSource<TFragment> source) =>
        _subjects.GetResourceContext(
            source,
            RuntimeModel<TModel, TFragment>.DefaultResourceContext
        );

    private ResourceId? GetResourceId(StateSource<TFragment> source) =>
        _subjects.GetResourceId(source, RuntimeModel<TModel, TFragment>.DefaultResourceContext);

    internal async ValueTask<StateWriteResult> WriteObservedAsync(
        StateSource<TFragment> source,
        ISourceWriter<TFragment> writer,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken
    )
    {
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticOperation.Write, source.Id);
        try
        {
            var context = GetResourceContext(source);
            var result = _subjects.Current is not null
                ? await source.WriteAsync(context, request, cancellationToken).ConfigureAwait(false)
                : await writer
                    .WriteAsync(context, request, cancellationToken)
                    .ConfigureAwait(false);
            diagnostic.Complete(
                ConfiglueDiagnosticEventKind.WriteCompleted,
                hasRevision: result.Revision is not null
            );
            return result;
        }
        catch (Exception exception)
        {
            diagnostic.Fail(
                exception is StateConflictException
                    ? ConfiglueDiagnosticEventKind.WriteConflict
                    : ConfiglueDiagnosticEventKind.WriteFailed,
                exception,
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            );
            throw;
        }
    }

    internal async ValueTask<StateWriteResult> WriteObservedBatchAsync(
        SourceId sourceId,
        IResourceBatchWriter writer,
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken
    )
    {
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticOperation.Write, sourceId);
        try
        {
            var result = await writer
                .WriteBatchAsync(mutations, cancellationToken)
                .ConfigureAwait(false);
            diagnostic.Complete(
                ConfiglueDiagnosticEventKind.WriteCompleted,
                hasRevision: result.Revision is not null
            );
            return result;
        }
        catch (Exception exception)
        {
            diagnostic.Fail(
                exception is StateConflictException
                    ? ConfiglueDiagnosticEventKind.WriteConflict
                    : ConfiglueDiagnosticEventKind.WriteFailed,
                exception,
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            );
            throw;
        }
    }

    private ValueTask<StateWriteResult> WriteSourceAsync(
        StateSource<TFragment> source,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken
    ) => WriteObservedAsync(source, source.Writer!, request, cancellationToken);

    /// <summary>One routed logical source write with its physical resource grouping.</summary>
    private readonly record struct PendingSourceWrite(
        StateSource<TFragment> Source,
        ISourceWriter<TFragment> Writer,
        StateWriteRequest<TFragment> Request,
        ResourceId? ResourceId,
        IResourceBatchWriter? BatchWriter,
        ResourceWriteMutation? Mutation
    );

    /// <summary>Write groups prepared without performing any physical write.</summary>
    /// <remarks>
    /// Each inner list executes as a single physical resource operation. Disposing releases
    /// the prepared batch plans; no physical write has occurred while the holder is alive.
    /// </remarks>
    private sealed class PreparedWriteGroups(
        List<List<PendingSourceWrite>> groups,
        DisposableBag owners
    ) : IDisposable
    {
        /// <summary>Each inner list executes as a single physical resource operation.</summary>
        public List<List<PendingSourceWrite>> Groups { get; } = groups;

        private DisposableBag? _owners = owners;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owners, null)?.Dispose();
        }
    }

    internal ValueTask<StateWriteReceipt> ApplyPatchesAsync(
        IEnumerable<StateSourcePatch> patches,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(patches);
        cancellationToken.ThrowIfCancellationRequested();
        return ApplyPatchesCoreAsync(patches.ToArray(), null, null, cancellationToken);
    }

    private async ValueTask<StateWriteReceipt> ApplyPatchesCoreAsync(
        StateSourcePatch[] patchRequests,
        StateRevisionVector? expectedBaselineRevisions,
        object? expectedResolvedModel,
        CancellationToken cancellationToken,
        ResolvedState<TModel, TFragment>? resolvedBaseline = null
    )
    {
        using var operation = _lifetime.EnterOperation();
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
            patchRequests.Select(static patch => patch.SourceId).Distinct().Count()
            != patchRequests.Length
        )
        {
            throw new ArgumentException(
                "A source can only appear once in a patch batch.",
                nameof(patchRequests)
            );
        }

        var modelSchema = RuntimeModel<TModel, TFragment>.Schema;
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

        using var prepared = await PrepareWriteGroupsAsync(
                patchRequests,
                expectedBaselineRevisions,
                expectedResolvedModel,
                cancellationToken,
                resolvedBaseline
            )
            .ConfigureAwait(false);
        var writeGroups = prepared.Groups;

        var results = new Dictionary<SourceId, StateSourceWriteResult>();
        var physicalWriteCount = 0;
        for (var groupIndex = 0; groupIndex < writeGroups.Count; groupIndex++)
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

            if (group.Count == 1)
            {
                var plan = group[0];
                StateWriteResult write;
                try
                {
                    write = await WriteSourceAsync(plan.Source, plan.Request, cancellationToken)
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
                catch (Exception exception)
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
                results.Add(
                    plan.Source.Id,
                    new StateSourceWriteResult(plan.Source.Id, plan.ResourceId, write.Revision)
                );
                physicalWriteCount++;
                continue;
            }

            var batchWriter = group[0].BatchWriter!;
            StateWriteResult batchResult;
            try
            {
                batchResult = await WriteObservedBatchAsync(
                        group[0].Source.Id,
                        batchWriter,
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
            catch (Exception exception)
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

        return new StateWriteReceipt(
            results.Values,
            physicalWriteCount,
            _stateName,
            _subjects.CurrentKey
        );

        StateMultiWriteException CreatePartialWriteException(
            Exception exception,
            List<PendingSourceWrite> failedGroup,
            IEnumerable<List<PendingSourceWrite>> remainingGroups,
            IEnumerable<StateSourceWriteResult> completed,
            int completedPhysicalWrites
        )
        {
            var failedPlan = failedGroup[0];
            return new StateMultiWriteException(
                new StateWriteReceipt(
                    completed,
                    completedPhysicalWrites,
                    _stateName,
                    _subjects.CurrentKey
                ),
                failedPlan.ResourceId,
                failedGroup.Select(static plan => plan.Source.Id),
                remainingGroups.SelectMany(static group => group.Select(plan => plan.Source.Id)),
                exception
            );
        }
    }

    /// <summary>
    /// Routes source patches, validates the proposed resolution, and groups the result by
    /// physical resource identity without performing any physical write.
    /// </summary>
    /// <remarks>
    /// Shared with the atomic write-plan preview so transports never duplicate routing logic.
    /// The caller owns the returned holder and must dispose it. Throwing before the first
    /// physical write is guaranteed: this method only reads sources and prepares in-memory
    /// batch mutations.
    /// </remarks>
    private async ValueTask<PreparedWriteGroups> PrepareWriteGroupsAsync(
        StateSourcePatch[] patchRequests,
        StateRevisionVector? expectedBaselineRevisions,
        object? expectedResolvedModel,
        CancellationToken cancellationToken,
        ResolvedState<TModel, TFragment>? resolvedBaseline = null
    )
    {
        var preparedPlanOwners = new DisposableBag();
        try
        {
            var baseline =
                resolvedBaseline
                ?? await _engine.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
            if (baseline.Result.Status != StateReadStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Configuration state could not be read: {baseline.Result.Status}."
                );
            }

            if (
                expectedBaselineRevisions is not null
                && !RuntimeState.HaveSameRevisions(
                    expectedBaselineRevisions,
                    baseline.Result.Revisions
                )
            )
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    "A state source changed after the configuration edit began."
                );
            }

            var replacements = new Dictionary<SourceId, StateReadResult<TFragment>>();
            var writePlans = new List<PendingSourceWrite>();

            foreach (var patchRequest in patchRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = _topology.FindSource(patchRequest.SourceId);
                if (!_topology.IsSourceActive(source.Id))
                {
                    throw new InvalidOperationException(
                        $"State source '{source.Id}' has been retired from this state instance."
                    );
                }

                var current =
                    TryGetPatchBaselineSourceResult(baseline, source)
                    ?? await _engine
                        .ReadSourceAsync(source, cancellationToken)
                        .ConfigureAwait(false);
                current = current.FromSource(source.Id, source.PhysicalOrigin);
                if (current.Status == StateReadStatus.Unavailable)
                {
                    throw new InvalidOperationException(
                        $"Cannot safely patch configuration because source '{source.Id}' is unavailable."
                    );
                }

                if (
                    baseline.Result.Revisions is null
                    || !baseline.Result.Revisions.TryGetRevision(
                        source.Id,
                        out var baselineRevision
                    )
                    || !string.Equals(current.Revision, baselineRevision, StringComparison.Ordinal)
                )
                {
                    throw RuntimeState.NewConflict(
                        _diagnostics,
                        $"State source '{source.Id}' changed while the patch batch was being prepared."
                    );
                }

                if (patchRequest.Patch.IsEmpty)
                {
                    continue;
                }

                if (source.Reader is CompositeStateSource<TFragment> composite)
                {
                    await PrepareCompositePatchAsync(
                            source,
                            composite,
                            patchRequest,
                            current,
                            baseline,
                            RuntimeModel<TModel, TFragment>.Schema,
                            replacements,
                            writePlans,
                            preparedPlanOwners,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    continue;
                }

                var sourceFragment = current.Status switch
                {
                    StateReadStatus.NotFound => RuntimeModel<TModel, TFragment>.EmptyFragment,
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
                    sourceFragment = await _engine
                        .MigrateFragmentAsync(sourceFragment, schema, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (patchRequest.Patch.Apply(sourceFragment) is not TFragment patchedFragment)
                {
                    throw new InvalidOperationException(
                        $"The patch for source '{source.Id}' returned an incompatible fragment."
                    );
                }

                patchedFragment = RuntimeModel<TModel, TFragment>.CloneFragment(patchedFragment);

                if (source.Writer is null)
                {
                    throw new InvalidOperationException(
                        $"State source '{source.Id}' does not support writes."
                    );
                }

                var request = new StateWriteRequest<TFragment>(
                    patchedFragment,
                    Condition: RevisionCondition.FromRevision(current.Revision)
                );
                var resourceContext = GetResourceContext(source);
                var sourceResourceId = GetResourceId(source);
                IResourceBatchWriter? batchWriter = null;
                ResourceWriteMutation? mutation = null;
                ResourceId? participantResourceId = null;
                if (source.Writer is IAsyncSourceWriteBatchParticipant<TFragment> asyncParticipant)
                {
                    var batchPlan = await asyncParticipant
                        .TryCreateBatchWriteAsync(resourceContext, request, cancellationToken)
                        .ConfigureAwait(false);
                    if (batchPlan is { } batch)
                    {
                        preparedPlanOwners.Add(batch);
                        participantResourceId = batch.ResourceId;
                        batchWriter = batch.BatchWriter;
                        mutation = batch.Mutation;
                    }
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
                        && batchIdentity.TryGetResourceId(
                            resourceContext,
                            out var batchWriterResourceId
                        )
                        && batchWriterResourceId != resolvedResourceId
                    )
                    {
                        throw new InvalidOperationException(
                            $"State source '{source.Id}' prepares a mutation for '{resolvedResourceId}' but its batch writer targets '{batchWriterResourceId}'."
                        );
                    }

                    sourceResourceId = resolvedResourceId;
                }

                var proposed = StateReadResult<TFragment>.Success(
                    patchedFragment,
                    current.Revision,
                    RuntimeModel<TModel, TFragment>.Schema.ToMetadata()
                ) with
                {
                    Revisions = current.Revisions,
                };
                replacements.Add(source.Id, proposed);
                writePlans.Add(
                    new PendingSourceWrite(
                        source,
                        source.Writer,
                        request,
                        sourceResourceId,
                        batchWriter,
                        mutation
                    )
                );
            }

            if (replacements.Count > 0)
            {
                var proposed = await _engine
                    .ResolveAsync(replacements, cancellationToken, captureContributions: true)
                    .ConfigureAwait(false);
                if (proposed.Result.Status != StateReadStatus.Success)
                {
                    throw new InvalidOperationException(
                        $"Patched configuration could not be resolved: {proposed.Result.Status}."
                    );
                }

                if (
                    expectedResolvedModel is TModel expectedModel
                    && RuntimeModel<TModel, TFragment>.Diff(proposed.Result.Value!, expectedModel)
                        is { IsEmpty: false } mismatch
                )
                {
                    var paths = GetReplaceMemberPaths(
                        RuntimeModel<TModel, TFragment>.Schema,
                        mismatch,
                        []
                    );
                    if (paths.Count > 0)
                    {
                        var readonlySources = proposed
                            .Contributions.Where(static contribution =>
                                contribution.Source.Writer is null
                            )
                            .Select(static contribution => contribution.Source.Id)
                            .Distinct()
                            .ToArray();
                        var details = string.Join(", ", paths);
                        var shadowing =
                            readonlySources.Length == 0
                                ? string.Empty
                                : $" Read-only source(s) contributing to the resolved state: '{string.Join("', '", readonlySources)}'.";
                        throw RuntimeState.NewConflict(
                            _diagnostics,
                            $"The configured source routes cannot realize the requested edit for '{details}'. A higher-priority contribution may shadow the write.{shadowing}"
                        );
                    }
                }

                if (
                    !RuntimeState.HaveSameRevisions(
                        baseline.Result.Revisions,
                        proposed.Result.Revisions
                    )
                )
                {
                    throw RuntimeState.NewConflict(
                        _diagnostics,
                        "A state source changed while the patch batch was being resolved."
                    );
                }

                _validation.Validate(proposed.Result.Value!);
            }

            var resourceGroups = new Dictionary<ResourceId, List<PendingSourceWrite>>();
            var independentWrites = new List<List<PendingSourceWrite>>();
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

            var writeGroups = resourceGroups.Values.Concat(independentWrites).ToList();
            foreach (var group in writeGroups.Where(static group => group.Count > 1))
            {
                if (group.Any(static plan => plan.BatchWriter is null || plan.Mutation is null))
                {
                    var sourceIds = string.Join(
                        "', '",
                        group.Select(static plan => plan.Source.Id)
                    );
                    throw new NotSupportedException(
                        $"Sources '{sourceIds}' share one ResourceId but their writers cannot batch physical mutations."
                    );
                }

                var canonicalWriter = group[0].BatchWriter!;
                for (var planIndex = 1; planIndex < group.Count; planIndex++)
                {
                    var plan = group[planIndex];
                    if (!ReferenceEquals(canonicalWriter, plan.BatchWriter!))
                    {
                        var sourceIds = string.Join("', '", group.Select(static p => p.Source.Id));
                        throw new NotSupportedException(
                            $"Sources '{sourceIds}' share ResourceId '{plan.ResourceId}' but expose distinct batch writer objects. Share one canonical batch writer for the physical resource so the batch executes atomically through a single writer."
                        );
                    }
                }

                ResourceWriteMutation.ValidateBatch(
                    group.Select(static plan => plan.Mutation!).ToArray()
                );
            }

            if (writePlans.Count > 0)
            {
                var latest = await _engine
                    .ReadPublicValueAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (
                    latest.Status != StateReadStatus.Success
                    || !RuntimeState.HaveSameRevisions(baseline.Result.Revisions, latest.Revisions)
                )
                {
                    throw RuntimeState.NewConflict(
                        _diagnostics,
                        "A state source changed before the patch batch could be written."
                    );
                }
            }

            var prepared = new PreparedWriteGroups(writeGroups, preparedPlanOwners);
            preparedPlanOwners = null!;
            return prepared;
        }
        finally
        {
            preparedPlanOwners?.Dispose();
        }
    }

    private StateReadResult<TFragment>? TryGetPatchBaselineSourceResult(
        ResolvedState<TModel, TFragment> baseline,
        StateSource<TFragment> source
    )
    {
        if (source.Reader is CompositeStateSource<TFragment>)
        {
            return null;
        }

        for (var index = 0; index < baseline.Contributions.Count; index++)
        {
            var contribution = baseline.Contributions[index];
            if (contribution.Source.Id != source.Id)
            {
                continue;
            }

            if (_engine.ReadValidationMode == ReadValidationMode.IgnoreValue)
            {
                return null;
            }

            // Contributions have already been migrated while resolving the baseline.
            return contribution.Result with
            {
                Schema = null,
            };
        }

        for (var index = 0; index < baseline.Failures.Count; index++)
        {
            var failure = baseline.Failures[index];
            if (failure.Source.Id == source.Id)
            {
                return failure.Result;
            }
        }

        return null;
    }

    internal async ValueTask<StateWriteReceipt> SaveAsync(
        IConfiglueModelPatch<TModel> patch,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = _lifetime.EnterOperation();
        ArgumentNullException.ThrowIfNull(patch);
        cancellationToken.ThrowIfCancellationRequested();
        var modelSchema = RuntimeModel<TModel, TFragment>.Schema;
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

        var fallbackSource = ResolveDefaultWriteSource();
        if (patch.IsEmpty)
        {
            if (fallbackSource is null)
            {
                return StateWriteReceipt.Empty;
            }

            var current = await _engine
                .ReadSourceAsync(fallbackSource, cancellationToken)
                .ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Cannot safely patch configuration because source '{fallbackSource.Id}' is unavailable."
                );
            }

            return StateWriteReceipt.Empty;
        }

        var baseline = await _engine
            .ResolveAsync(null, cancellationToken, captureContributions: true)
            .ConfigureAwait(false);
        if (baseline.Result.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {baseline.Result.Status}."
            );
        }

        var baselineFragment =
            baseline.MergedFragment
            ?? RuntimeModel<TModel, TFragment>.ToFragment(baseline.Result.Value!);
        if (patch.Apply(baselineFragment) is not TFragment requestedFragment)
        {
            throw new InvalidOperationException(
                "The patch returned an incompatible configuration fragment."
            );
        }

        var expectedResolvedModel = _cloner.Clone(
            RuntimeModel<TModel, TFragment>.FromFragment(requestedFragment)
        );

        if (_writePlan.PropertyRoutes.Count > 0)
        {
            ValidateWritePlan(_writePlan);
        }

        IReadOnlyDictionary<SourceId, IConfigluePatch> patchesBySource;
        if (_writePlan.PropertyRoutes.Count == 0 && fallbackSource is not null)
        {
            // With a single default writable source every member routes to it, so the
            // per-member routing work can be skipped entirely.
            patchesBySource = new Dictionary<SourceId, IConfigluePatch>()
            {
                [fallbackSource.Id] = patch,
            };
        }
        else if (patch is IConfiglueRoutablePatch routablePatch)
        {
            patchesBySource = routablePatch.Route(_writePlan, _writePlan.DefaultSourceId);
        }
        else if (patch is IConfiglueDynamicMemberPatch memberPatch)
        {
            var routed = new Dictionary<SourceId, List<int>>();
            foreach (var member in modelSchema.Members)
            {
                var selected = memberPatch.SelectMembers([member.Id]);
                if (selected.IsEmpty)
                {
                    continue;
                }

                var targetSourceId = _writePlan.ResolveSourceIdOrNull(
                    ConfiglueMemberPath.Root(modelSchema).Append(member.Id)
                );
                if (targetSourceId is null)
                {
                    throw new InvalidOperationException(
                        $"No writable source owns '{member.Name}'. Configure a root write target or an explicit write plan."
                    );
                }
                var resolvedTargetSourceId = targetSourceId.Value;
                if (!routed.TryGetValue(resolvedTargetSourceId, out var memberIds))
                {
                    memberIds = [];
                    routed.Add(resolvedTargetSourceId, memberIds);
                }

                memberIds.Add(member.Id);
            }

            patchesBySource =
                routed.Count == 0
                    ? new Dictionary<SourceId, IConfigluePatch>()
                    {
                        [
                            fallbackSource?.Id
                                ?? throw new InvalidOperationException(
                                    "No writable source owns this patch. Configure a root write target or an explicit write plan."
                                )
                        ] = patch,
                    }
                    : routed.ToDictionary(
                        static route => route.Key,
                        route => memberPatch.SelectMembers(route.Value.ToArray()),
                        EqualityComparer<SourceId>.Default
                    );
        }
        else
        {
            patchesBySource = new Dictionary<SourceId, IConfigluePatch>()
            {
                [
                    fallbackSource?.Id
                        ?? throw new InvalidOperationException(
                            "No writable source owns this patch. Configure a root write target or an explicit write plan."
                        )
                ] = patch,
            };
        }

        var sourcePatches = patchesBySource
            .Select(static route => new StateSourcePatch(route.Key, route.Value))
            .ToArray();
        var result = await ApplyPatchesCoreAsync(
                sourcePatches,
                baseline.Result.Revisions,
                expectedResolvedModel,
                cancellationToken,
                baseline
            )
            .ConfigureAwait(false);
        return result;
    }

    internal async ValueTask<StateWriteReceipt> WriteChangesToSourcesAsync(
        TModel before,
        TModel after,
        StateRevisionVector? expectedBaselineRevisions,
        IReadOnlyList<ResolvedContribution<TFragment>> baselineContributions,
        StateWritePlan writePlan,
        CancellationToken cancellationToken
    )
    {
        using var operation = _lifetime.EnterOperation();
        after = _cloner.Clone(after);
        _validation.Validate(after);
        var changes = RuntimeModel<TModel, TFragment>.Diff(before, after);
        if (changes.IsEmpty)
        {
            return StateWriteReceipt.Empty;
        }

        var routedChanges = PartitionRoutedChanges(
            RuntimeModel<TModel, TFragment>.Schema,
            changes,
            after,
            ConfiglueMemberPath.Root(RuntimeModel<TModel, TFragment>.Schema),
            writePlan
        );
        var patches = CreateRoutedPatches(routedChanges, after, baselineContributions);
        if (patches.Length == 0)
        {
            return StateWriteReceipt.Empty;
        }

        return await ApplyPatchesCoreAsync(
                patches,
                expectedBaselineRevisions,
                after,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private StateSourcePatch[] CreateRoutedPatches(
        Dictionary<SourceId, IConfiglueFragment> routedChanges,
        TModel after,
        IReadOnlyList<ResolvedContribution<TFragment>> baselineContributions
    )
    {
        var patches = new List<StateSourcePatch>(routedChanges.Count);
        StateSource<TFragment>[]? reversedActiveSources = null;
        foreach (var (sourceId, sourceChanges) in routedChanges)
        {
            var plannedChanges = (TFragment)PlanMergeAwareChanges(
                RuntimeModel<TModel, TFragment>.Schema,
                sourceChanges,
                after,
                [],
                sourceId,
                baselineContributions,
                NeedsSourceOrder(RuntimeModel<TModel, TFragment>.Schema, sourceChanges)
                    ? reversedActiveSources ??= _topology.GetReversedActiveSources()
                    : null
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

    // NOTE: GetReplaceMemberPaths below builds dotted strings, but it runs only on
    // the write-conflict error path (PatchApplication mismatch diagnostics), never in
    // normal schema-bound routing. Per-member hot-path routing stays in ID space above.
    private static List<string> GetReplaceMemberPaths(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        List<string> path
    )
    {
        var paths = new List<string>();
        foreach (var change in changes.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, change.Id, out var member))
            {
                continue;
            }

            path.Add(member.Name);
            if (
                member.MergeMode == MergeMode.Deep
                && member.NestedSchemaFactory is not null
                && change.Value is IConfiglueFragment nestedChanges
            )
            {
                paths.AddRange(
                    GetReplaceMemberPaths(member.NestedSchemaFactory(), nestedChanges, path)
                );
            }
            else if (member.MergeMode == MergeMode.Replace)
            {
                paths.Add(string.Join(".", path));
            }

            path.RemoveAt(path.Count - 1);
        }

        return paths;
    }

    private Dictionary<SourceId, IConfiglueFragment> PartitionRoutedChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        object afterModel,
        ConfiglueMemberPath path,
        StateWritePlan writePlan
    )
    {
        var routed = new Dictionary<SourceId, IConfiglueFragment>();
        foreach (var change in changes.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, change.Id, out var member))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.Id}' has no member with id {change.Id}."
                );
            }

            var propertyPath = path.Append(member.Id);
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
                    propertyPath,
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
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"The edit replaces nested member '{propertyPath}' with null, so its more specific source routes cannot be applied."
                );
            }

            var targetSourceId =
                writePlan.ResolveSourceIdOrNull(propertyPath)
                ?? throw new InvalidOperationException(
                    $"No writable source owns '{propertyPath}'. Configure a default write owner or an explicit write route."
                );
            var targetFragment = routed.TryGetValue(targetSourceId, out var existing)
                ? existing
                : schema.CreateEmptyFragment();
            routed[targetSourceId] = targetFragment.WithMember(member.Id, change.Value);
        }

        return routed;
    }

    private Dictionary<SourceId, IConfiglueFragment> PartitionCompositeChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        CompositeStateSource<TFragment> composite,
        ConfiglueMemberPath path,
        StateWritePlan boundCompositePlan
    )
    {
        var routed = new Dictionary<SourceId, IConfiglueFragment>();
        foreach (var change in changes.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, change.Id, out var member))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.Id}' has no member with id {change.Id}."
                );
            }

            // Generated-ID routing: extend the compiled member path with the member ID
            // and resolve against the pre-bound composite plan. No dotted strings are
            // built per member and no names are re-resolved through Split/lookup.
            var propertyPath = path.Append(member.Id);
            if (
                member.NestedSchemaFactory is not null
                && change.Value is IConfiglueFragment nestedChanges
                && composite.HasWriteRouteBelow(propertyPath, boundCompositePlan)
            )
            {
                var nestedRouted = PartitionCompositeChanges(
                    member.NestedSchemaFactory(),
                    nestedChanges,
                    composite,
                    propertyPath,
                    boundCompositePlan
                );
                foreach (var (nestedComponentId, nestedFragment) in nestedRouted)
                {
                    var componentChanges = routed.TryGetValue(nestedComponentId, out var existing)
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
                && composite.HasWriteRouteBelow(propertyPath, boundCompositePlan)
            )
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"The edit replaces nested member '{propertyPath}' as a whole, so its more specific composite component routes cannot be applied."
                );
            }

            var targetComponentId = composite
                .ResolveWriteComponent(propertyPath, boundCompositePlan)
                .Id;
            var targetChanges = routed.TryGetValue(targetComponentId, out var current)
                ? current
                : schema.CreateEmptyFragment();
            routed[targetComponentId] = targetChanges.WithMember(member.Id, change.Value);
        }

        return routed;
    }

    private static bool NeedsSourceOrder(ConfiglueModelSchema schema, IConfiglueFragment changes)
    {
        foreach (var change in changes.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, change.Id, out var member))
            {
                continue;
            }

            if (member.MergeStrategy is not null)
            {
                return true;
            }

            if (
                member.CollectionValueFactory is not null
                && (member.MergeMode == MergeMode.Append || member.MergeMode == MergeMode.SetUnion)
            )
            {
                return true;
            }

            if (
                member.NestedSchemaFactory is not null
                && change.Value is IConfiglueFragment nested
                && NeedsSourceOrder(member.NestedSchemaFactory(), nested)
            )
            {
                return true;
            }
        }

        return false;
    }

    private IConfiglueFragment PlanMergeAwareChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        object? afterModel,
        List<string> path,
        SourceId targetSourceId,
        IReadOnlyList<ResolvedContribution<TFragment>> contributions,
        StateSource<TFragment>[]? sourceOrder
    )
    {
        foreach (var change in changes.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, change.Id, out var member))
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
                            contributions,
                            sourceOrder
                        )
                    );
                    continue;
                }

                if (member.MergeStrategy is { } mergeStrategy)
                {
                    var strategySources = sourceOrder!;
                    var strategyValues = new ConfiglueMergeSourceValue[strategySources.Length];
                    for (var index = 0; index < strategySources.Length; index++)
                    {
                        var source = strategySources[index];
                        var value = Optional<object?>.Missing;
                        foreach (var contribution in contributions)
                        {
                            if (
                                contribution.Source.Id == source.Id
                                && contribution.Result.Value is { } sourceValue
                                && RuntimeState.TryGetFragmentValue(
                                    sourceValue,
                                    path,
                                    out var contributionValue
                                )
                            )
                            {
                                value = Optional<object?>.Present(contributionValue);
                                break;
                            }
                        }

                        strategyValues[index] = new ConfiglueMergeSourceValue(source.Id, value);
                    }

                    if (mergeStrategy is not IConfiglueMergeContributionPlanner planner)
                    {
                        throw RuntimeState.NewConflict(
                            _diagnostics,
                            $"The custom merge strategy for '{member.Name}' does not support source contribution planning."
                        );
                    }

                    if (
                        !planner.TryPlanSourceContributionObject(
                            strategyValues,
                            targetSourceId,
                            afterValue,
                            out var targetContribution,
                            out var reason
                        )
                    )
                    {
                        throw RuntimeState.NewConflict(
                            _diagnostics,
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
                var orderedSources = sourceOrder!;
                var targetIndex = Array.FindIndex(
                    orderedSources,
                    candidate => candidate.Id == targetSourceId
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
                        orderedSources,
                        targetIndex,
                        valuesBySource,
                        desired,
                        member.Name
                    ),
                    MergeMode.SetUnion => PlanSetUnionContribution(
                        orderedSources,
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

    private static Dictionary<SourceId, List<object?>> GetCollectionContributions(
        IReadOnlyList<string> path,
        IReadOnlyList<ResolvedContribution<TFragment>> contributions
    )
    {
        var valuesBySource = new Dictionary<SourceId, List<object?>>();
        foreach (var contribution in contributions)
        {
            if (
                !RuntimeState.TryGetFragmentValue(contribution.Result.Value!, path, out var value)
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
        IReadOnlyDictionary<SourceId, List<object?>> valuesBySource,
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

        if (desired.Count < prefix.Count + suffix.Count)
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"The edit to append-merged member '{memberName}' cannot be represented by source '{sourceOrder[targetIndex].Id}' while preserving other source contributions."
            );
        }

        var comparer = EqualityComparer<object?>.Default;
        var prefixMatches = true;
        for (var index = 0; index < prefix.Count; index++)
        {
            if (!comparer.Equals(desired[index], prefix[index]))
            {
                prefixMatches = false;
                break;
            }
        }

        var suffixMatches = true;
        var suffixOffset = desired.Count - suffix.Count;
        for (var index = 0; index < suffix.Count; index++)
        {
            if (!comparer.Equals(desired[suffixOffset + index], suffix[index]))
            {
                suffixMatches = false;
                break;
            }
        }

        if (!prefixMatches || !suffixMatches)
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"The edit to append-merged member '{memberName}' cannot be represented by source '{sourceOrder[targetIndex].Id}' while preserving other source contributions."
            );
        }

        var contributionCount = desired.Count - prefix.Count - suffix.Count;
        var targetValues = new List<object?>(contributionCount);
        for (var index = 0; index < contributionCount; index++)
        {
            targetValues.Add(desired[prefix.Count + index]);
        }

        return targetValues;
    }

    private List<object?> PlanSetUnionContribution(
        IReadOnlyList<StateSource<TFragment>> sourceOrder,
        int targetIndex,
        IReadOnlyDictionary<SourceId, List<object?>> valuesBySource,
        IReadOnlyList<object?> desired,
        ConfiglueMemberSchema member
    )
    {
        var comparer = EqualityComparer<object?>.Default;
        var otherValues = new HashSet<object?>(comparer);
        for (var index = 0; index < sourceOrder.Count; index++)
        {
            if (
                index == targetIndex
                || !valuesBySource.TryGetValue(sourceOrder[index].Id, out var otherSourceValues)
            )
            {
                continue;
            }

            for (var valueIndex = 0; valueIndex < otherSourceValues.Count; valueIndex++)
            {
                otherValues.Add(otherSourceValues[valueIndex]);
            }
        }

        var desiredSet = new HashSet<object?>(comparer);
        for (var index = 0; index < desired.Count; index++)
        {
            desiredSet.Add(desired[index]);
        }

        if (otherValues.Any(value => !desiredSet.Contains(value)))
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"The edit to set-union member '{member.Name}' removes a value contributed by another source."
            );
        }

        var targetValues = new List<object?>();
        for (var index = 0; index < desired.Count; index++)
        {
            if (!otherValues.Contains(desired[index]))
            {
                targetValues.Add(desired[index]);
            }
        }

        var merged = new List<object?>();
        var mergedSet = new HashSet<object?>(comparer);
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

            merged.AddRange(values.Where(value => mergedSet.Add(value)));
        }

        var isSet =
            member.ValueType.IsGenericType
            && (
                member.ValueType.GetGenericTypeDefinition() == typeof(ISet<>)
                || member.ValueType.GetGenericTypeDefinition().FullName
                    == "System.Collections.Generic.IReadOnlySet`1"
                || member.ValueType.GetGenericTypeDefinition() == typeof(HashSet<>)
            );
        var matchesDesired = isSet
            ? merged.Count == desiredSet.Count && merged.All(desiredSet.Contains)
            : merged.SequenceEqual(desired);
        if (!matchesDesired)
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"The edit to set-union member '{member.Name}' cannot be represented by source '{sourceOrder[targetIndex].Id}' while preserving other source contributions."
            );
        }

        return targetValues;
    }

    private async ValueTask PrepareCompositePatchAsync(
        StateSource<TFragment> source,
        CompositeStateSource<TFragment> composite,
        StateSourcePatch patchRequest,
        StateReadResult<TFragment> current,
        ResolvedState<TModel, TFragment> baseline,
        ConfiglueModelSchema modelSchema,
        Dictionary<SourceId, StateReadResult<TFragment>> replacements,
        List<PendingSourceWrite> writePlans,
        DisposableBag preparedPlanOwners,
        CancellationToken cancellationToken
    )
    {
        if (patchRequest.Patch is not IConfiglueDynamicMemberPatch memberPatch)
        {
            throw new NotSupportedException(
                $"Patch for composite source '{source.Id}' must support member selection."
            );
        }

        // Bind once per schema (cached inside the composite source): route strings are
        // parsed a single time and every per-member lookup below stays in generated-ID
        // space. GetBoundWritePlan also rejects unknown route paths with an
        // InvalidOperationException naming the offending route.
        var boundCompositePlan = composite.GetBoundWritePlan(modelSchema);

        var routedPatches = new Dictionary<SourceId, IConfigluePatch>();
        if (patchRequest.Patch is FragmentChangesPatch fragmentPatch)
        {
            var routedChanges = PartitionCompositeChanges(
                modelSchema,
                fragmentPatch.Changes,
                composite,
                ConfiglueMemberPath.Root(modelSchema),
                boundCompositePlan
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
            var routedMemberIds = new Dictionary<SourceId, List<int>>();
            var compositeRoot = ConfiglueMemberPath.Root(modelSchema);
            foreach (var member in modelSchema.Members)
            {
                var selected = memberPatch.SelectMembers([member.Id]);
                if (selected.IsEmpty)
                {
                    continue;
                }

                // Single-level dynamic fallback stays in ID space too: the compiled
                // path avoids name-based re-resolution through Split/lookup.
                var memberPath = compositeRoot.Append(member.Id);
                if (composite.HasWriteRouteBelow(memberPath, boundCompositePlan))
                {
                    throw new NotSupportedException(
                        $"Patch for nested member '{member.Name}' must use a model edit so its nested changes can be routed."
                    );
                }

                var component = composite.ResolveWriteComponent(memberPath, boundCompositePlan);
                if (!routedMemberIds.TryGetValue(component.Id, out var memberIds))
                {
                    memberIds = [];
                    routedMemberIds.Add(component.Id, memberIds);
                }

                memberIds.Add(member.Id);
            }

            foreach (var (componentId, memberIds) in routedMemberIds)
            {
                routedPatches.Add(componentId, memberPatch.SelectMembers(memberIds.ToArray()));
            }
        }

        if (routedPatches.Count == 0)
        {
            return;
        }

        if (
            baseline.Result.Revisions is null
            || !baseline.Result.Revisions.TryGetNestedRevisions(source.Id, out var nestedBaseline)
            || nestedBaseline is null
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"Composite source '{source.Id}' has no component revision baseline."
            );
        }

        var componentOverrides = new Dictionary<SourceId, TFragment>();
        foreach (var (componentId, componentPatch) in routedPatches)
        {
            var component = composite.Components.First(item => item.Id == componentId);
            var componentCurrent = (
                await _engine.ReadSourceAsync(component, cancellationToken).ConfigureAwait(false)
            ).FromSource(component.Id, component.PhysicalOrigin);
            if (componentCurrent.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Cannot safely patch configuration because component '{component.Id}' is unavailable."
                );
            }

            ValidateCompositeComponentBaseline(nestedBaseline, component, componentCurrent);

            var componentFragment = componentCurrent.Status switch
            {
                StateReadStatus.NotFound => RuntimeModel<TModel, TFragment>.EmptyFragment,
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
                componentFragment = await _engine
                    .MigrateFragmentAsync(componentFragment, componentSchema, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (componentPatch.Apply(componentFragment) is not TFragment patchedComponent)
            {
                throw new InvalidOperationException(
                    $"The patch for component '{component.Id}' returned an incompatible fragment."
                );
            }

            patchedComponent = RuntimeModel<TModel, TFragment>.CloneFragment(patchedComponent);

            componentOverrides.Add(component.Id, patchedComponent);
            var componentRequest = new StateWriteRequest<TFragment>(
                patchedComponent,
                Condition: RevisionCondition.FromRevision(componentCurrent.Revision)
            );
            var resourceContext = GetResourceContext(component);
            var componentResourceId = GetResourceId(component);
            IResourceBatchWriter? componentBatchWriter = null;
            ResourceWriteMutation? componentMutation = null;
            ResourceId? componentParticipantResourceId = null;
            if (
                component.Writer
                is IAsyncSourceWriteBatchParticipant<TFragment> componentParticipant
            )
            {
                var batchPlan = await componentParticipant
                    .TryCreateBatchWriteAsync(resourceContext, componentRequest, cancellationToken)
                    .ConfigureAwait(false);
                if (batchPlan is { } prepared)
                {
                    preparedPlanOwners.Add(prepared);
                    componentParticipantResourceId = prepared.ResourceId;
                    componentBatchWriter = prepared.BatchWriter;
                    componentMutation = prepared.Mutation;
                }
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
                    && batchIdentity.TryGetResourceId(
                        resourceContext,
                        out var batchWriterResourceId
                    )
                    && batchWriterResourceId != componentResolvedResourceId
                )
                {
                    throw new InvalidOperationException(
                        $"State source '{component.Id}' prepares a mutation for '{componentResolvedResourceId}' but its batch writer targets '{batchWriterResourceId}'."
                    );
                }

                componentResourceId = componentResolvedResourceId;
            }

            writePlans.Add(
                new PendingSourceWrite(
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
                await _engine.ReadSourceAsync(component, cancellationToken).ConfigureAwait(false)
            ).FromSource(component.Id, component.PhysicalOrigin);
            if (componentState.Status != StateReadStatus.Success || componentState.Value is null)
            {
                continue;
            }

            var componentValue = componentState.Value;
            if (componentState.Schema is { } componentSchema)
            {
                componentValue = await _engine
                    .MigrateFragmentAsync(componentValue, componentSchema, cancellationToken)
                    .ConfigureAwait(false);
            }

            componentOverrides.Add(component.Id, componentValue);
        }

        var composed = await composite
            .ReadWithOverridesAsync(
                componentOverrides,
                GetResourceContext(source),
                cancellationToken
            )
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
        return;
    }

    private void ValidateCompositeComponentBaseline(
        StateRevisionVector nestedBaseline,
        StateSource<TFragment> component,
        StateReadResult<TFragment> componentCurrent
    )
    {
        if (!nestedBaseline.TryGetRevision(component.Id, out var expectedRevision))
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"Component source '{component.Id}' changed while the patch batch was being prepared."
            );
        }

        var hasExpectedNested =
            nestedBaseline.TryGetNestedRevisions(component.Id, out var expectedNested)
            && expectedNested is not null;
        var currentNested = componentCurrent.Revisions;
        if (hasExpectedNested != (currentNested is not null))
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"Component source '{component.Id}' changed while the patch batch was being prepared."
            );
        }

        if (currentNested is not null)
        {
            if (
                !string.Equals(
                    expectedRevision,
                    componentCurrent.Revision,
                    StringComparison.Ordinal
                ) || !RuntimeState.HaveSameRevisions(expectedNested, currentNested)
            )
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Component source '{component.Id}' changed while the patch batch was being prepared."
                );
            }

            return;
        }

        if (!string.Equals(expectedRevision, componentCurrent.Revision, StringComparison.Ordinal))
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"Component source '{component.Id}' changed while the patch batch was being prepared."
            );
        }
    }

    internal async ValueTask<StateWritePreview> PreviewWriteAsync(
        TModel desired,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = _lifetime.EnterOperation();
        ArgumentNullException.ThrowIfNull(desired);
        cancellationToken.ThrowIfCancellationRequested();

        var baseline = await _engine
            .ResolveAsync(null, cancellationToken, captureContributions: true)
            .ConfigureAwait(false);
        if (baseline.Result.Status != StateReadStatus.Success || baseline.Result.Value is null)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {baseline.Result.Status}."
            );
        }

        var changes = RuntimeModel<TModel, TFragment>.Diff(baseline.Result.Value, desired);
        if (changes.IsEmpty)
        {
            return StateWritePreview.Empty;
        }

        var routedChanges = PartitionRoutedChanges(
            RuntimeModel<TModel, TFragment>.Schema,
            changes,
            desired,
            ConfiglueMemberPath.Root(RuntimeModel<TModel, TFragment>.Schema),
            _writePlan
        );
        var patches = CreateRoutedPatches(routedChanges, desired, baseline.Contributions);
        if (patches.Length == 0)
        {
            return StateWritePreview.Empty;
        }

        using var prepared = await PrepareWriteGroupsAsync(
                patches,
                baseline.Result.Revisions,
                desired,
                cancellationToken,
                baseline
            )
            .ConfigureAwait(false);
        return new StateWritePreview(prepared.Groups.Count, isEmpty: false);
    }

    internal async ValueTask<StateWritePreview> PreviewForSubjectAsync(
        IConfiglueSubject subject,
        TModel desired,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        using var scope = _subjects.Enter(subject);
        return await PreviewWriteAsync(desired, cancellationToken).ConfigureAwait(false);
    }

    private sealed class FragmentChangesPatch(TFragment changes) : IConfiglueDynamicMemberPatch
    {
        public TFragment Changes => changes;

        public ConfiglueModelSchema Schema => RuntimeModel<TModel, TFragment>.Schema;

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
            var selected = RuntimeModel<TModel, TFragment>.EmptyFragment;
            foreach (var member in changes.EnumeratePresentMembersFast())
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
}
