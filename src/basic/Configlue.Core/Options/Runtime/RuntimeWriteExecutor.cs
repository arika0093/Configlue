using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Owns observed physical writes for one runtime: single-source writes, shared-resource
/// batch writes, and atomic multi-group execution.
///
/// The executor never reads sources and never plans. It receives prepared write groups
/// (see <c>RuntimeWritePreparer</c>) and executes each inner group as a single physical
/// resource operation, preserving writer exception types and subject routing.
/// </summary>
internal sealed class RuntimeWriteExecutor<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeSubjectContext _subjects;
    private readonly string _stateName;

    internal RuntimeWriteExecutor(
        RuntimeDiagnosticRecorder diagnostics,
        RuntimeSubjectContext subjects,
        string stateName
    )
    {
        _diagnostics = diagnostics;
        _subjects = subjects;
        _stateName = stateName;
    }

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

    private ConfiglueResourceContext GetResourceContext(StateSource<TFragment> source) =>
        _subjects.GetResourceContext(
            source,
            RuntimeModel<TModel, TFragment>.DefaultResourceContext
        );

    /// <summary>
    /// Executes prepared write groups in order. Each group is one physical resource
    /// operation; a failure after the first physical write is reported as a
    /// <see cref="StateMultiWriteException"/> carrying the partial receipt.
    /// </summary>
    internal async ValueTask<StateWriteReceipt> ExecuteGroupsAsync(
        List<List<PendingSourceWrite<TFragment>>> writeGroups,
        CancellationToken cancellationToken
    )
    {
        var results = new Dictionary<SourceId, StateSourceWriteResult>();
        var physicalWriteCount = 0;
        for (var groupIndex = 0; groupIndex < writeGroups.Count; groupIndex++)
        {
            var group = writeGroups[groupIndex];
            ThrowIfCanceledMidBatch(
                group,
                writeGroups,
                groupIndex,
                results,
                physicalWriteCount,
                cancellationToken
            );

            if (group.Count == 1)
            {
                var revision = await ExecuteSingleWriteAsync(
                        group,
                        writeGroups,
                        groupIndex,
                        results,
                        physicalWriteCount,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                results.Add(
                    group[0].Source.Id,
                    new StateSourceWriteResult(group[0].Source.Id, group[0].ResourceId, revision)
                );
                physicalWriteCount++;
                continue;
            }

            var batchRevision = await ExecuteBatchWriteAsync(
                    group,
                    writeGroups,
                    groupIndex,
                    results,
                    physicalWriteCount,
                    cancellationToken
                )
                .ConfigureAwait(false);
            foreach (var plan in group)
            {
                results.Add(
                    plan.Source.Id,
                    new StateSourceWriteResult(plan.Source.Id, plan.ResourceId, batchRevision)
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
    }

    private void ThrowIfCanceledMidBatch(
        List<PendingSourceWrite<TFragment>> group,
        List<List<PendingSourceWrite<TFragment>>> writeGroups,
        int groupIndex,
        Dictionary<SourceId, StateSourceWriteResult> results,
        int physicalWriteCount,
        CancellationToken cancellationToken
    )
    {
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
    }

    private async ValueTask<string?> ExecuteSingleWriteAsync(
        List<PendingSourceWrite<TFragment>> group,
        List<List<PendingSourceWrite<TFragment>>> writeGroups,
        int groupIndex,
        Dictionary<SourceId, StateSourceWriteResult> results,
        int physicalWriteCount,
        CancellationToken cancellationToken
    )
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

        return write.Revision;
    }

    private async ValueTask<string?> ExecuteBatchWriteAsync(
        List<PendingSourceWrite<TFragment>> group,
        List<List<PendingSourceWrite<TFragment>>> writeGroups,
        int groupIndex,
        Dictionary<SourceId, StateSourceWriteResult> results,
        int physicalWriteCount,
        CancellationToken cancellationToken
    )
    {
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

        return batchResult.Revision;
    }

    private StateMultiWriteException CreatePartialWriteException(
        Exception exception,
        List<PendingSourceWrite<TFragment>> failedGroup,
        IEnumerable<List<PendingSourceWrite<TFragment>>> remainingGroups,
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
