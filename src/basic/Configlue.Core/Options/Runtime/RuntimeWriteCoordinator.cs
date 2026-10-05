using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Thin runtime-facing entry point for writes: save/apply/preview.
///
/// Planning lives in <see cref="RuntimeWritePlanner{TModel, TFragment}"/>, preparation
/// (baseline reads, optimistic concurrency, composite handling, grouping) in
/// <see cref="RuntimeWritePreparer{TModel, TFragment}"/>, and physical execution in
/// <see cref="RuntimeWriteExecutor{TModel, TFragment}"/>. This type only owns lifetime
/// scopes, subject scopes, and the composition of those collaborators.
/// </summary>
internal sealed class RuntimeWriteCoordinator<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeWritePlanner<TModel, TFragment> _planner;
    private readonly RuntimeWritePreparer<TModel, TFragment> _preparer;
    private readonly RuntimeWriteExecutor<TModel, TFragment> _executor;
    private readonly RuntimeLifetime _lifetime;
    private readonly RuntimeSubjectContext _subjects;

    internal RuntimeWriteCoordinator(
        RuntimeWritePlanner<TModel, TFragment> planner,
        RuntimeWritePreparer<TModel, TFragment> preparer,
        RuntimeWriteExecutor<TModel, TFragment> executor,
        RuntimeLifetime lifetime,
        RuntimeSubjectContext subjects
    )
    {
        _planner = planner;
        _preparer = preparer;
        _executor = executor;
        _lifetime = lifetime;
        _subjects = subjects;
    }

    internal StateWritePlan Plan => _planner.Plan;

    internal bool DefaultWriteSourceIsInferred => _planner.DefaultWriteSourceIsInferred;

    internal void ValidateWritePlan(StateWritePlan writePlan) =>
        _planner.ValidateWritePlan(writePlan);

    internal ValueTask<StateWriteResult> WriteObservedAsync(
        StateSource<TFragment> source,
        ISourceWriter<TFragment> writer,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken
    ) => _executor.WriteObservedAsync(source, writer, request, cancellationToken);

    internal ValueTask<StateWriteResult> WriteObservedBatchAsync(
        SourceId sourceId,
        IResourceBatchWriter writer,
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken
    ) => _executor.WriteObservedBatchAsync(sourceId, writer, mutations, cancellationToken);

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
        using var prepared = await _preparer
            .PrepareWriteGroupsAsync(
                patchRequests,
                expectedBaselineRevisions,
                expectedResolvedModel,
                cancellationToken,
                resolvedBaseline
            )
            .ConfigureAwait(false);
        return await _executor
            .ExecuteGroupsAsync(prepared.Groups, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<StateWriteReceipt> SaveAsync(
        IConfiglueModelPatch<TModel> patch,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = _lifetime.EnterOperation();
        ArgumentNullException.ThrowIfNull(patch);
        cancellationToken.ThrowIfCancellationRequested();
        _planner.ValidatePatchSchema(patch);

        using var prepared = await _preparer
            .PrepareSaveGroupsAsync(patch, cancellationToken)
            .ConfigureAwait(false);
        if (prepared is null)
        {
            return StateWriteReceipt.Empty;
        }

        return await _executor
            .ExecuteGroupsAsync(prepared.Groups, cancellationToken)
            .ConfigureAwait(false);
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
        using var prepared = await _preparer
            .PrepareChangesGroupsAsync(
                before,
                after,
                expectedBaselineRevisions,
                baselineContributions,
                writePlan,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (prepared is null)
        {
            return StateWriteReceipt.Empty;
        }

        return await _executor
            .ExecuteGroupsAsync(prepared.Groups, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<StateWritePreview> PreviewWriteAsync(
        TModel desired,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = _lifetime.EnterOperation();
        ArgumentNullException.ThrowIfNull(desired);
        cancellationToken.ThrowIfCancellationRequested();

        using var prepared = await _preparer
            .PreparePreviewGroupsAsync(desired, cancellationToken)
            .ConfigureAwait(false);
        if (prepared is null)
        {
            return StateWritePreview.Empty;
        }

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
}
