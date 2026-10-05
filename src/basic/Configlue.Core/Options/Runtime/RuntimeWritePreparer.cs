using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Owns write preparation (preflight) for one runtime: baseline reads, optimistic-concurrency
/// checks, composite-source preparation, proposed-resolution validation, physical-resource
/// grouping, and atomicity decisions.
///
/// Preparation never performs a physical write: every stage only reads sources and builds
/// in-memory batch mutations. Throwing before the first physical write is guaranteed.
/// Pure routing/partitioning math is delegated to <c>RuntimeWritePlanner</c>; execution to
/// <c>RuntimeWriteExecutor</c>.
/// </summary>
internal sealed class RuntimeWritePreparer<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeWritePlanner<TModel, TFragment> _planner;
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeSubjectContext _subjects;
    private readonly RuntimeValidationPipeline<TModel, TFragment> _validation;
    private readonly RuntimeModelCloner<TModel, TFragment> _cloner;

    internal RuntimeWritePreparer(
        RuntimeWritePlanner<TModel, TFragment> planner,
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeDiagnosticRecorder diagnostics,
        RuntimeSubjectContext subjects,
        RuntimeValidationPipeline<TModel, TFragment> validation,
        RuntimeModelCloner<TModel, TFragment> cloner
    )
    {
        _planner = planner;
        _engine = engine;
        _diagnostics = diagnostics;
        _subjects = subjects;
        _validation = validation;
        _cloner = cloner;
    }

    private ConfiglueResourceContext GetResourceContext(StateSource<TFragment> source) =>
        _subjects.GetResourceContext(
            source,
            RuntimeModel<TModel, TFragment>.DefaultResourceContext
        );

    private ResourceId? GetResourceId(StateSource<TFragment> source) =>
        _subjects.GetResourceId(source, RuntimeModel<TModel, TFragment>.DefaultResourceContext);

    /// <summary>Prepared single-source write: the physical plan plus its proposed resolution.</summary>
    private sealed record PreparedSourceWrite(
        PendingSourceWrite<TFragment> Plan,
        StateReadResult<TFragment> Proposed
    );

    /// <summary>Prepared composite-source writes and the composed replacement for the parent.</summary>
    private sealed record CompositePreparation(
        List<PendingSourceWrite<TFragment>> Plans,
        SourceId SourceId,
        StateReadResult<TFragment> Proposed
    );

    /// <summary>Per-component preparation output for one composite source.</summary>
    private sealed record CompositeComponentPreparation(
        List<PendingSourceWrite<TFragment>> Plans,
        Dictionary<SourceId, TFragment> ComponentOverrides
    );

    /// <summary>Batch-participation probe output with reconciled physical resource identity.</summary>
    private sealed record PreparedBatchWrite(
        ResourceId? ResourceId,
        IResourceBatchWriter? BatchWriter,
        ResourceWriteMutation? Mutation
    );

    /// <summary>
    /// Prepares a model save end-to-end: baseline resolution, expected-model snapshot,
    /// patch routing, and write-group preparation. Returns null when the patch is empty.
    /// </summary>
    internal async ValueTask<PreparedWriteGroups<TFragment>?> PrepareSaveGroupsAsync(
        IConfiglueModelPatch<TModel> patch,
        CancellationToken cancellationToken
    )
    {
        var fallbackSource = _planner.ResolveDefaultWriteSource();
        if (patch.IsEmpty)
        {
            await PrepareEmptySaveAsync(fallbackSource, cancellationToken).ConfigureAwait(false);
            return null;
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

        if (_planner.Plan.PropertyRoutes.Count > 0)
        {
            _planner.ValidateWritePlan(_planner.Plan);
        }

        var patchesBySource = _planner.RouteSavePatches(patch, fallbackSource);
        var sourcePatches = patchesBySource
            .Select(static route => new StateSourcePatch(route.Key, route.Value))
            .ToArray();
        return await PrepareWriteGroupsAsync(
                sourcePatches,
                baseline.Result.Revisions,
                expectedResolvedModel,
                cancellationToken,
                baseline
            )
            .ConfigureAwait(false);
    }

    private async ValueTask PrepareEmptySaveAsync(
        StateSource<TFragment>? fallbackSource,
        CancellationToken cancellationToken
    )
    {
        if (fallbackSource is null)
        {
            return;
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
    }

    /// <summary>
    /// Prepares model-diff writes end-to-end: cloning, validation, routing, merge-aware
    /// planning, and write-group preparation. Returns null when there is nothing to write.
    /// </summary>
    internal async ValueTask<PreparedWriteGroups<TFragment>?> PrepareChangesGroupsAsync(
        TModel before,
        TModel after,
        StateRevisionVector? expectedBaselineRevisions,
        IReadOnlyList<ResolvedContribution<TFragment>> baselineContributions,
        StateWritePlan writePlan,
        CancellationToken cancellationToken
    )
    {
        after = _cloner.Clone(after);
        _validation.Validate(after);
        var changes = RuntimeModel<TModel, TFragment>.Diff(before, after);
        if (changes.IsEmpty)
        {
            return null;
        }

        var routedChanges = _planner.PartitionRoutedChanges(
            RuntimeModel<TModel, TFragment>.Schema,
            changes,
            after,
            ConfiglueMemberPath.Root(RuntimeModel<TModel, TFragment>.Schema),
            writePlan
        );
        var patches = _planner.CreateRoutedPatches(routedChanges, after, baselineContributions);
        if (patches.Length == 0)
        {
            return null;
        }

        return await PrepareWriteGroupsAsync(
                patches,
                expectedBaselineRevisions,
                after,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Prepares a write preview end-to-end without performing any physical write.
    /// Returns null when the desired model already matches the resolved state.
    /// </summary>
    internal async ValueTask<PreparedWriteGroups<TFragment>?> PreparePreviewGroupsAsync(
        TModel desired,
        CancellationToken cancellationToken
    )
    {
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
            return null;
        }

        var routedChanges = _planner.PartitionRoutedChanges(
            RuntimeModel<TModel, TFragment>.Schema,
            changes,
            desired,
            ConfiglueMemberPath.Root(RuntimeModel<TModel, TFragment>.Schema),
            _planner.Plan
        );
        var patches = _planner.CreateRoutedPatches(routedChanges, desired, baseline.Contributions);
        if (patches.Length == 0)
        {
            return null;
        }

        return await PrepareWriteGroupsAsync(
                patches,
                baseline.Result.Revisions,
                desired,
                cancellationToken,
                baseline
            )
            .ConfigureAwait(false);
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
    internal async ValueTask<PreparedWriteGroups<TFragment>> PrepareWriteGroupsAsync(
        StateSourcePatch[] patchRequests,
        StateRevisionVector? expectedBaselineRevisions,
        object? expectedResolvedModel,
        CancellationToken cancellationToken,
        ResolvedState<TModel, TFragment>? resolvedBaseline = null
    )
    {
        ValidatePatchBatch(patchRequests);
        var preparedPlanOwners = new DisposableBag();
        try
        {
            var baseline =
                resolvedBaseline
                ?? await _engine.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
            EnsureBaselineReadable(baseline);
            EnsureExpectedBaselineRevisions(expectedBaselineRevisions, baseline);

            var replacements = new Dictionary<SourceId, StateReadResult<TFragment>>();
            var writePlans = new List<PendingSourceWrite<TFragment>>();
            await PrepareSourcePatchesAsync(
                    patchRequests,
                    baseline,
                    replacements,
                    writePlans,
                    preparedPlanOwners,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await ValidateProposedResolutionAsync(
                    replacements,
                    expectedResolvedModel,
                    baseline,
                    cancellationToken
                )
                .ConfigureAwait(false);

            var writeGroups = GroupByPhysicalResource(writePlans);
            EnsureAtomicBatches(writeGroups);
            await VerifyPreWriteRevisionsAsync(baseline, writePlans.Count > 0, cancellationToken)
                .ConfigureAwait(false);

            var prepared = new PreparedWriteGroups<TFragment>(writeGroups, preparedPlanOwners);
            preparedPlanOwners = null!;
            return prepared;
        }
        finally
        {
            preparedPlanOwners?.Dispose();
        }
    }

    private static void ValidatePatchBatch(StateSourcePatch[] patchRequests)
    {
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
    }

    private static void EnsureBaselineReadable(ResolvedState<TModel, TFragment> baseline)
    {
        if (baseline.Result.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {baseline.Result.Status}."
            );
        }
    }

    private void EnsureExpectedBaselineRevisions(
        StateRevisionVector? expectedBaselineRevisions,
        ResolvedState<TModel, TFragment> baseline
    )
    {
        if (
            expectedBaselineRevisions is not null
            && !RuntimeState.HaveSameRevisions(expectedBaselineRevisions, baseline.Result.Revisions)
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                "A state source changed after the configuration edit began."
            );
        }
    }

    private async ValueTask PrepareSourcePatchesAsync(
        StateSourcePatch[] patchRequests,
        ResolvedState<TModel, TFragment> baseline,
        Dictionary<SourceId, StateReadResult<TFragment>> replacements,
        List<PendingSourceWrite<TFragment>> writePlans,
        DisposableBag preparedPlanOwners,
        CancellationToken cancellationToken
    )
    {
        foreach (var patchRequest in patchRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = _planner.FindSource(patchRequest.SourceId);
            if (!_planner.IsSourceActive(source.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' has been retired from this state instance."
                );
            }

            var current = await ReadPatchBaselineAsync(source, baseline, cancellationToken)
                .ConfigureAwait(false);

            if (source.Reader is CompositeStateSource<TFragment> composite)
            {
                var compositePreparation = await PrepareCompositePatchAsync(
                        source,
                        composite,
                        patchRequest,
                        current,
                        baseline,
                        preparedPlanOwners,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (compositePreparation is null)
                {
                    continue;
                }

                writePlans.AddRange(compositePreparation.Plans);
                replacements.Add(compositePreparation.SourceId, compositePreparation.Proposed);
                continue;
            }

            var prepared = await PrepareSingleSourceWriteAsync(
                    source,
                    patchRequest,
                    current,
                    preparedPlanOwners,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (prepared is null)
            {
                continue;
            }

            replacements.Add(prepared.Plan.Source.Id, prepared.Proposed);
            writePlans.Add(prepared.Plan);
        }
    }

    /// <summary>Reads one source baseline with optimistic-concurrency protection.</summary>
    private async ValueTask<StateReadResult<TFragment>> ReadPatchBaselineAsync(
        StateSource<TFragment> source,
        ResolvedState<TModel, TFragment> baseline,
        CancellationToken cancellationToken
    )
    {
        var current =
            TryGetPatchBaselineSourceResult(baseline, source)
            ?? await _engine.ReadSourceAsync(source, cancellationToken).ConfigureAwait(false);
        current = current.FromSource(source.Id, source.PhysicalOrigin);
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
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"State source '{source.Id}' changed while the patch batch was being prepared."
            );
        }

        return current;
    }

    private static StateReadResult<TFragment>? TryGetPatchBaselineSourceResult(
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

    /// <summary>Prepares one ordinary (non-composite) source write. Returns null when skipped.</summary>
    private async ValueTask<PreparedSourceWrite?> PrepareSingleSourceWriteAsync(
        StateSource<TFragment> source,
        StateSourcePatch patchRequest,
        StateReadResult<TFragment> current,
        DisposableBag preparedPlanOwners,
        CancellationToken cancellationToken
    )
    {
        if (patchRequest.Patch.IsEmpty)
        {
            return null;
        }

        var patchedFragment = await PreparePatchedFragmentAsync(
                source,
                current,
                patchRequest.Patch,
                isCompositeComponent: false,
                cancellationToken
            )
            .ConfigureAwait(false);

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
        var batch = await TryPrepareBatchWriteAsync(
                source,
                request,
                GetResourceId(source),
                GetResourceContext(source),
                preparedPlanOwners,
                cancellationToken
            )
            .ConfigureAwait(false);

        var proposed = StateReadResult<TFragment>.Success(
            patchedFragment,
            current.Revision,
            RuntimeModel<TModel, TFragment>.Schema.ToMetadata()
        ) with
        {
            Revisions = current.Revisions,
        };
        return new PreparedSourceWrite(
            new PendingSourceWrite<TFragment>(
                source,
                source.Writer,
                request,
                batch.ResourceId,
                batch.BatchWriter,
                batch.Mutation
            ),
            proposed
        );
    }

    /// <summary>
    /// Migrates, patches, and clones one source fragment. Shared by ordinary sources and
    /// composite components; only the diagnostic wording differs.
    /// </summary>
    private async ValueTask<TFragment> PreparePatchedFragmentAsync(
        StateSource<TFragment> source,
        StateReadResult<TFragment> current,
        IConfigluePatch patch,
        bool isCompositeComponent,
        CancellationToken cancellationToken
    )
    {
        var sourceFragment = current.Status switch
        {
            StateReadStatus.NotFound => RuntimeModel<TModel, TFragment>.EmptyFragment,
            StateReadStatus.Success => current.Value
                ?? throw new InvalidOperationException(
                    isCompositeComponent
                        ? $"Component source '{source.Id}' returned a null fragment."
                        : $"State source '{source.Id}' returned a null configuration fragment."
                ),
            _ => throw new InvalidOperationException(
                isCompositeComponent
                    ? $"Component source '{source.Id}' could not be patched: {current.Status}."
                    : $"Source '{source.Id}' could not be patched: {current.Status}."
            ),
        };
        if (current.Schema is { } schema)
        {
            sourceFragment = await _engine
                .MigrateFragmentAsync(sourceFragment, schema, cancellationToken)
                .ConfigureAwait(false);
        }

        if (patch.Apply(sourceFragment) is not TFragment patchedFragment)
        {
            throw new InvalidOperationException(
                isCompositeComponent
                    ? $"The patch for component '{source.Id}' returned an incompatible fragment."
                    : $"The patch for source '{source.Id}' returned an incompatible fragment."
            );
        }

        return RuntimeModel<TModel, TFragment>.CloneFragment(patchedFragment);
    }

    /// <summary>
    /// Probes batch participation for one writer and reconciles the declared physical
    /// resource identity with the writer-provided one. Prepared batch plans are owned
    /// by <paramref name="preparedPlanOwners"/> so failures dispose them.
    /// </summary>
    private static async ValueTask<PreparedBatchWrite> TryPrepareBatchWriteAsync(
        StateSource<TFragment> source,
        StateWriteRequest<TFragment> request,
        ResourceId? declaredResourceId,
        ConfiglueResourceContext resourceContext,
        DisposableBag preparedPlanOwners,
        CancellationToken cancellationToken
    )
    {
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

        var resourceId = declaredResourceId;
        if (participantResourceId is { } resolvedResourceId)
        {
            if (resourceId is { } declared && declared != resolvedResourceId)
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' declares resource '{declared}' but its writer targets '{resolvedResourceId}'."
                );
            }

            if (
                batchWriter is IResourceIdentity batchIdentity
                && batchIdentity.TryGetResourceId(resourceContext, out var batchWriterResourceId)
                && batchWriterResourceId != resolvedResourceId
            )
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' prepares a mutation for '{resolvedResourceId}' but its batch writer targets '{batchWriterResourceId}'."
                );
            }

            resourceId = resolvedResourceId;
        }

        return new PreparedBatchWrite(resourceId, batchWriter, mutation);
    }

    private async ValueTask ValidateProposedResolutionAsync(
        Dictionary<SourceId, StateReadResult<TFragment>> replacements,
        object? expectedResolvedModel,
        ResolvedState<TModel, TFragment> baseline,
        CancellationToken cancellationToken
    )
    {
        if (replacements.Count == 0)
        {
            return;
        }

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
            ThrowUnrealizableEditConflict(proposed, mismatch);
        }

        if (!RuntimeState.HaveSameRevisions(baseline.Result.Revisions, proposed.Result.Revisions))
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                "A state source changed while the patch batch was being resolved."
            );
        }

        _validation.Validate(proposed.Result.Value!);
    }

    private void ThrowUnrealizableEditConflict(
        ResolvedState<TModel, TFragment> proposed,
        IConfiglueFragment mismatch
    )
    {
        var paths = RuntimeWritePlanner<TModel, TFragment>.GetReplaceMemberPaths(
            RuntimeModel<TModel, TFragment>.Schema,
            mismatch,
            []
        );
        if (paths.Count > 0)
        {
            var readonlySources = proposed
                .Contributions.Where(static contribution => contribution.Source.Writer is null)
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

    private static List<List<PendingSourceWrite<TFragment>>> GroupByPhysicalResource(
        List<PendingSourceWrite<TFragment>> writePlans
    )
    {
        var resourceGroups = new Dictionary<ResourceId, List<PendingSourceWrite<TFragment>>>();
        var independentWrites = new List<List<PendingSourceWrite<TFragment>>>();
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

        return resourceGroups.Values.Concat(independentWrites).ToList();
    }

    private static void EnsureAtomicBatches(List<List<PendingSourceWrite<TFragment>>> writeGroups)
    {
        foreach (var group in writeGroups.Where(static group => group.Count > 1))
        {
            if (group.Any(static plan => plan.BatchWriter is null || plan.Mutation is null))
            {
                var sourceIds = string.Join("', '", group.Select(static plan => plan.Source.Id));
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
    }

    private async ValueTask VerifyPreWriteRevisionsAsync(
        ResolvedState<TModel, TFragment> baseline,
        bool hasWrites,
        CancellationToken cancellationToken
    )
    {
        if (!hasWrites)
        {
            return;
        }

        var latest = await _engine.ReadPublicValueAsync(cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<CompositePreparation?> PrepareCompositePatchAsync(
        StateSource<TFragment> source,
        CompositeStateSource<TFragment> composite,
        StateSourcePatch patchRequest,
        StateReadResult<TFragment> current,
        ResolvedState<TModel, TFragment> baseline,
        DisposableBag preparedPlanOwners,
        CancellationToken cancellationToken
    )
    {
        var modelSchema = RuntimeModel<TModel, TFragment>.Schema;
        var routedPatches = RouteCompositePatches(source, composite, patchRequest, modelSchema);
        if (routedPatches.Count == 0)
        {
            return null;
        }

        var nestedBaseline = RequireCompositeBaseline(baseline, source);
        var components = await PrepareCompositeComponentsAsync(
                composite,
                routedPatches,
                nestedBaseline,
                preparedPlanOwners,
                cancellationToken
            )
            .ConfigureAwait(false);
        var proposed = await ComposeCompositeReplacementAsync(
                source,
                composite,
                current,
                components.ComponentOverrides,
                modelSchema,
                cancellationToken
            )
            .ConfigureAwait(false);
        return new CompositePreparation(components.Plans, source.Id, proposed);
    }

    /// <summary>Routes a composite patch to per-component patches. No source I/O.</summary>
    private Dictionary<SourceId, IConfigluePatch> RouteCompositePatches(
        StateSource<TFragment> source,
        CompositeStateSource<TFragment> composite,
        StateSourcePatch patchRequest,
        ConfiglueModelSchema modelSchema
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
        if (patchRequest.Patch is RuntimeFragmentChangesPatch<TModel, TFragment> fragmentPatch)
        {
            var routedChanges = _planner.PartitionCompositeChanges(
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
                    new RuntimeFragmentChangesPatch<TModel, TFragment>((TFragment)componentChanges)
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
            RouteCompositeDynamicPatch(
                composite,
                memberPatch,
                modelSchema,
                boundCompositePlan,
                routedPatches
            );
        }

        return routedPatches;
    }

    private static void RouteCompositeDynamicPatch(
        CompositeStateSource<TFragment> composite,
        IConfiglueDynamicMemberPatch memberPatch,
        ConfiglueModelSchema modelSchema,
        StateWritePlan boundCompositePlan,
        Dictionary<SourceId, IConfigluePatch> routedPatches
    )
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

    private StateRevisionVector RequireCompositeBaseline(
        ResolvedState<TModel, TFragment> baseline,
        StateSource<TFragment> source
    )
    {
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

        return nestedBaseline;
    }

    private async ValueTask<CompositeComponentPreparation> PrepareCompositeComponentsAsync(
        CompositeStateSource<TFragment> composite,
        Dictionary<SourceId, IConfigluePatch> routedPatches,
        StateRevisionVector nestedBaseline,
        DisposableBag preparedPlanOwners,
        CancellationToken cancellationToken
    )
    {
        var writePlans = new List<PendingSourceWrite<TFragment>>();
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

            var patchedComponent = await PreparePatchedFragmentAsync(
                    component,
                    componentCurrent,
                    componentPatch,
                    isCompositeComponent: true,
                    cancellationToken
                )
                .ConfigureAwait(false);

            componentOverrides.Add(component.Id, patchedComponent);
            var componentRequest = new StateWriteRequest<TFragment>(
                patchedComponent,
                Condition: RevisionCondition.FromRevision(componentCurrent.Revision)
            );
            var batch = await TryPrepareBatchWriteAsync(
                    component,
                    componentRequest,
                    GetResourceId(component),
                    GetResourceContext(component),
                    preparedPlanOwners,
                    cancellationToken
                )
                .ConfigureAwait(false);

            writePlans.Add(
                new PendingSourceWrite<TFragment>(
                    component,
                    component.Writer!,
                    componentRequest,
                    batch.ResourceId,
                    batch.BatchWriter,
                    batch.Mutation
                )
            );
        }

        return new CompositeComponentPreparation(writePlans, componentOverrides);
    }

    /// <summary>Re-reads untouched components and composes the parent replacement.</summary>
    private async ValueTask<StateReadResult<TFragment>> ComposeCompositeReplacementAsync(
        StateSource<TFragment> source,
        CompositeStateSource<TFragment> composite,
        StateReadResult<TFragment> current,
        Dictionary<SourceId, TFragment> componentOverrides,
        ConfiglueModelSchema modelSchema,
        CancellationToken cancellationToken
    )
    {
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

        return StateReadResult<TFragment>.Success(
            composed.Value,
            current.Revision,
            modelSchema.ToMetadata()
        ) with
        {
            Revisions = composed.Revisions,
        };
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
}
