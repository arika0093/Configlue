using Configlue.CompilerServices;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private async ValueTask<StateWriteResult> WriteObservedAsync(
        StateSource<TFragment> source,
        ISourceWriter<TFragment> writer,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken
    )
    {
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticEventKind.WriteStarted, source.Id);
        try
        {
            var context = GetResourceContext(source);
            var result = _subjectContext.Value is not null
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

    private async ValueTask<StateWriteResult> WriteObservedBatchAsync(
        SourceId sourceId,
        IResourceBatchWriter writer,
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken
    )
    {
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticEventKind.WriteStarted, sourceId);
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

    private async ValueTask<(StateReadResult<TModel> Result, bool ValueChanged)> ReadReloadAsync(
        TModel previousEffective,
        bool hasEffective,
        CancellationToken cancellationToken
    )
    {
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticEventKind.ReloadStarted);
        try
        {
            var result = await ReadPublicValueAsync(cancellationToken, diagnostic.Id)
                .ConfigureAwait(false);
            var changed =
                result.Status == StateReadStatus.Success
                && (!hasEffective || !Diff(previousEffective, result.Value!).IsEmpty);
            if (changed)
            {
                _diagnostics.Record(
                    ConfiglueDiagnosticEventKind.EffectiveValueChanged,
                    diagnostic.Id,
                    effectiveValueChanged: true
                );
            }
            diagnostic.Complete(
                result.Status == StateReadStatus.Success
                    ? ConfiglueDiagnosticEventKind.ReloadCompleted
                    : ConfiglueDiagnosticEventKind.ReloadFailed,
                result.Status,
                result.Revision is not null,
                changed
            );
            return (result, changed);
        }
        catch (Exception exception)
        {
            diagnostic.Fail(
                ConfiglueDiagnosticEventKind.ReloadFailed,
                exception,
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            );
            throw;
        }
    }
}
