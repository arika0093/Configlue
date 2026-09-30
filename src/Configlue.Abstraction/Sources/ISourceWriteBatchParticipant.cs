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
}

/// <summary>Prepares batch writes for a logical subject and source-specific key.</summary>
public interface IContextualSourceWriteBatchParticipant<T> : ISourceWriteBatchParticipant<T>
{
    /// <summary>Prepares a batch write for one logical subject and source-specific key.</summary>
    bool TryCreateBatchWrite(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        out ResourceId resourceId,
        out IResourceBatchWriter? batchWriter,
        out ResourceWriteMutation? mutation
    );
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
}

/// <summary>Asynchronously prepares a source batch write for one logical subject and key.</summary>
public interface IContextualAsyncSourceWriteBatchParticipant<T>
    : IAsyncSourceWriteBatchParticipant<T>
{
    /// <summary>Asynchronously prepares a batch write for one logical subject and key.</summary>
    ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    );
}
