namespace Configlue;

/// <summary>Prepares a typed source write for batching with other sources sharing one physical resource.</summary>
public interface IStateWriteBatchParticipant<T>
{
    /// <summary>Creates the physical resource mutation for this typed write when batching is supported.</summary>
    bool TryCreateBatchWrite(
        StateWriteRequest<T> request,
        out ResourceId resourceId,
        out IResourceBatchWriter? batchWriter,
        out ResourceWriteMutation? mutation
    );
}

/// <summary>A prepared typed source mutation for a batch-capable physical resource.</summary>
public readonly record struct StateWriteBatchPlan(
    ResourceId ResourceId,
    IResourceBatchWriter BatchWriter,
    ResourceWriteMutation Mutation
);

/// <summary>Asynchronously prepares a typed source write for batching with sibling sources.</summary>
public interface IAsyncStateWriteBatchParticipant<T>
{
    /// <summary>Whether this source can prepare a batch write for its underlying physical resource.</summary>
    bool CanPrepareBatchWrite { get; }

    /// <summary>Creates the physical resource mutation for this typed write when batching is supported.</summary>
    ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    );
}
