using Configlue.Resources;
using Configlue.State;

namespace Configlue.Sources;

/// <summary>Prepares a typed source write for batching with other sources sharing one physical resource.</summary>
public interface ISourceWriteBatchParticipant<T>
{
    /// <summary>Creates the physical resource mutation for this typed write when batching is supported.</summary>
    bool TryCreateBatchWrite(
        StateWriteRequest<T> request,
        out ResourceId resourceId,
        out IResourceBatchWriter? batchWriter,
        out ResourceWriteMutation? mutation
    );

    /// <summary>Prepares a batch write for one logical subject and source-specific key.</summary>
    bool TryCreateBatchWrite(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        out ResourceId resourceId,
        out IResourceBatchWriter? batchWriter,
        out ResourceWriteMutation? mutation
    )
    {
        if (!TryCreateBatchWrite(request, out resourceId, out batchWriter, out mutation))
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
}

/// <summary>Asynchronously prepares a typed source write for batching with sibling sources.</summary>
public interface IAsyncSourceWriteBatchParticipant<T>
{
    /// <summary>Whether this source can prepare a batch write for its underlying physical resource.</summary>
    bool CanPrepareBatchWrite { get; }

    /// <summary>Creates the physical resource mutation for this typed write when batching is supported.</summary>
    ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    );

    /// <summary>Asynchronously prepares a batch write for one logical subject and key.</summary>
    async ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        var plan = await TryCreateBatchWriteAsync(request, cancellationToken).ConfigureAwait(false);
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
