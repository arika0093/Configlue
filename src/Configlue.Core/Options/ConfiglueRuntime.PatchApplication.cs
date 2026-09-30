using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Configlue.CompilerServices;
using Configlue.Sources;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <inheritdoc />
    public ValueTask<StateWriteReceipt> ApplyPatchesAsync(
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
        ResolvedState? resolvedBaseline = null
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

        var modelSchema = ModelSchema;
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

        var baseline =
            resolvedBaseline
            ?? await ResolveCoreAsync(null, cancellationToken).ConfigureAwait(false);
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
        var writePlans =
            new List<(
                StateSource<TFragment> Source,
                ISourceWriter<TFragment> Writer,
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
                    $"State source '{source.Id}' has been retired from this state instance."
                );
            }

            var current =
                TryGetPatchBaselineSourceResult(baseline, source)
                ?? await ReadSourceAsync(source, cancellationToken).ConfigureAwait(false);
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
                throw LogConflict(
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
                        modelSchema,
                        replacements,
                        writePlans,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                continue;
            }

            var sourceFragment = current.Status switch
            {
                StateReadStatus.NotFound => EmptyFragment,
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

            patchedFragment = CloneFragment(patchedFragment);

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
            if (
                source.Writer is IAsyncSourceWriteBatchParticipant<TFragment>
                {
                    CanPrepareBatchWrite: true,
                } asyncParticipant
            )
            {
                var batchPlan = await asyncParticipant
                    .TryCreateBatchWriteAsync(resourceContext, request, cancellationToken)
                    .ConfigureAwait(false);
                if (batchPlan is { } prepared)
                {
                    participantResourceId = prepared.ResourceId;
                    batchWriter = prepared.BatchWriter;
                    mutation = prepared.Mutation;
                }
            }
            else if (
                source.Writer is ISourceWriteBatchParticipant<TFragment> participant
                && participant.TryCreateBatchWrite(
                    resourceContext,
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
                modelSchema.ToMetadata()
            ) with
            {
                Revisions = current.Revisions,
            };
            replacements.Add(source.Id, proposed);
            writePlans.Add(
                (source, source.Writer, request, sourceResourceId, batchWriter, mutation)
            );
        }

        if (replacements.Count > 0)
        {
            var proposed = await ResolveCoreAsync(
                    replacements,
                    cancellationToken,
                    captureContributions: true
                )
                .ConfigureAwait(false);
            if (proposed.Result.Status != StateReadStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Patched configuration could not be resolved: {proposed.Result.Status}."
                );
            }

            if (
                expectedResolvedModel is TModel expectedModel
                && Diff(proposed.Result.Value!, expectedModel) is { IsEmpty: false } mismatch
            )
            {
                var paths = GetReplaceMemberPaths(ModelSchema, mismatch, []);
                if (paths.Count > 0)
                {
                    var readonlySources = proposed
                        .Contributions.Where(static contribution =>
                            contribution.Source.Writer is null
                        )
                        .Select(static contribution => contribution.Source.Id)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var details = string.Join(", ", paths);
                    var shadowing =
                        readonlySources.Length == 0
                            ? string.Empty
                            : $" Read-only source(s) contributing to the resolved state: '{string.Join("', '", readonlySources)}'.";
                    throw LogConflict(
                        $"The configured source routes cannot realize the requested edit for '{details}'. A higher-priority contribution may shadow the write.{shadowing}"
                    );
                }
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
                    ISourceWriter<TFragment> Writer,
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
                    ISourceWriter<TFragment> Writer,
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

        var results = new Dictionary<string, StateSourceWriteResult>(StringComparer.Ordinal);
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
                    "Writing configuration state for {ModelType} state {StateName} through source {SourceId} at resource {ResourceId}.",
                    typeof(TModel).FullName,
                    _stateName,
                    plan.Source.Id,
                    resourceId
                );
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
#pragma warning disable S2139
                catch (Exception exception)
                {
                    _logger?.LogError(
                        PhysicalWriteFailedEvent,
                        exception,
                        "Writing configuration state failed for {ModelType} state {StateName} through source {SourceId} at resource {ResourceId}.",
                        typeof(TModel).FullName,
                        _stateName,
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
                results.Add(
                    plan.Source.Id,
                    new StateSourceWriteResult(plan.Source.Id, plan.ResourceId, write.Revision)
                );
                _logger?.LogDebug(
                    PhysicalWriteEvent,
                    "Wrote configuration state through source {SourceId} at resource {ResourceId} for {ModelType} state {StateName}.",
                    plan.Source.Id,
                    resourceId,
                    typeof(TModel).FullName,
                    _stateName
                );
                physicalWriteCount++;
                continue;
            }

            var batchWriter = group[0].BatchWriter!;
            _logger?.LogInformation(
                PhysicalWriteEvent,
                "Writing a physical batch for {ModelType} state {StateName} through sources {SourceIds} at resource {ResourceId}.",
                typeof(TModel).FullName,
                _stateName,
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
                    "The physical batch write failed for {ModelType} state {StateName} through sources {SourceIds} at resource {ResourceId}.",
                    typeof(TModel).FullName,
                    _stateName,
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
                "Wrote one physical batch for {ModelType} state {StateName} through sources {SourceIds} at resource {ResourceId}.",
                typeof(TModel).FullName,
                _stateName,
                sourceIds,
                resourceId
            );
        }

        return new StateWriteReceipt(results.Values, physicalWriteCount);

        static StateMultiWriteException CreatePartialWriteException(
            Exception exception,
            List<(
                StateSource<TFragment> Source,
                ISourceWriter<TFragment> Writer,
                StateWriteRequest<TFragment> Request,
                ResourceId? ResourceId,
                IResourceBatchWriter? BatchWriter,
                ResourceWriteMutation? Mutation
            )> failedGroup,
            IEnumerable<
                List<(
                    StateSource<TFragment> Source,
                    ISourceWriter<TFragment> Writer,
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
                new StateWriteReceipt(completed, completedPhysicalWrites),
                failedPlan.ResourceId,
                failedGroup.Select(static plan => plan.Source.Id),
                remainingGroups.SelectMany(static group => group.Select(plan => plan.Source.Id)),
                exception
            );
        }
    }

    private StateReadResult<TFragment>? TryGetPatchBaselineSourceResult(
        ResolvedState baseline,
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
            if (!string.Equals(contribution.Source.Id, source.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (_readValidationMode == ReadValidationMode.IgnoreValue)
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
            if (string.Equals(failure.Source.Id, source.Id, StringComparison.Ordinal))
            {
                return failure.Result;
            }
        }

        return null;
    }
}
