using Configlue.Resources;
using Configlue.State;

namespace Configlue.Sources;

/// <summary>Context-aware source operations with portable fallback behavior.</summary>
public static class SourceContextExtensions
{
    /// <summary>Reads state for a source-specific subject key.</summary>
    public static ValueTask<StateReadResult<T>> ReadAsync<T>(
        this ISourceReader<T> reader,
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }
        return reader is IContextualSourceReader<T> contextualReader
            ? contextualReader.ReadAsync(context, cancellationToken)
            : reader.ReadAsync(cancellationToken);
    }

    /// <summary>Writes state for a source-specific subject key.</summary>
    public static ValueTask<StateWriteResult> WriteAsync<T>(
        this ISourceWriter<T> writer,
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }
        return writer is IContextualSourceWriter<T> contextualWriter
            ? contextualWriter.WriteAsync(context, request, cancellationToken)
            : writer.WriteAsync(request, cancellationToken);
    }

    /// <summary>Waits for changes to one source-specific subject key.</summary>
    public static ValueTask WaitForChangeAsync(
        this ISourceWatcher watcher,
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        if (watcher is null)
        {
            throw new ArgumentNullException(nameof(watcher));
        }
        return watcher is IContextualSourceWatcher contextualWatcher
            ? contextualWatcher.WaitForChangeAsync(context, observedRevision, cancellationToken)
            : watcher.WaitForChangeAsync(observedRevision, cancellationToken);
    }

    /// <summary>Prepares a batch write for one logical subject and source-specific key.</summary>
    public static bool TryCreateBatchWrite<T>(
        this ISourceWriteBatchParticipant<T> participant,
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        out ResourceId resourceId,
        out IResourceBatchWriter? batchWriter,
        out ResourceWriteMutation? mutation
    )
    {
        if (participant is null)
        {
            throw new ArgumentNullException(nameof(participant));
        }
        if (participant is IContextualSourceWriteBatchParticipant<T> contextualParticipant)
        {
            return contextualParticipant.TryCreateBatchWrite(
                context,
                request,
                out resourceId,
                out batchWriter,
                out mutation
            );
        }

        if (
            !participant.TryCreateBatchWrite(request, out resourceId, out batchWriter, out mutation)
        )
        {
            return false;
        }

        if (
            batchWriter is IResourceIdentity identity
            && identity.TryGetResourceId(context, out var contextualResourceId)
        )
        {
            resourceId = contextualResourceId;
        }

        mutation = mutation?.WithContext(context);
        return true;
    }

    /// <summary>Asynchronously prepares a batch write for one logical subject and key.</summary>
    public static async ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync<T>(
        this IAsyncSourceWriteBatchParticipant<T> participant,
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        if (participant is null)
        {
            throw new ArgumentNullException(nameof(participant));
        }
        if (participant is IContextualAsyncSourceWriteBatchParticipant<T> contextualParticipant)
        {
            return await contextualParticipant
                .TryCreateBatchWriteAsync(context, request, cancellationToken)
                .ConfigureAwait(false);
        }

        var plan = await participant
            .TryCreateBatchWriteAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (plan is not { } prepared)
        {
            return null;
        }

        var resourceId = prepared.ResourceId;
        if (prepared.BatchWriter.TryGetResourceId(context, out var contextualResourceId))
        {
            resourceId = contextualResourceId;
        }

        return prepared with
        {
            ResourceId = resourceId,
            Mutation = prepared.Mutation.WithContext(context),
        };
    }
}
