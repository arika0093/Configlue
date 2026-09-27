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
public readonly record struct StateWriteBatchPlan
{
    /// <summary>Gets or initializes the <see cref="ResourceId"/> value.</summary>
    public ResourceId ResourceId { get; init; }

    /// <summary>Gets or initializes the <see cref="BatchWriter"/> value.</summary>
    public IResourceBatchWriter BatchWriter { get; init; }

    /// <summary>Gets or initializes the <see cref="Mutation"/> value.</summary>
    public ResourceWriteMutation Mutation { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="ResourceId">The initial value for the <see cref="ResourceId"/> property.</param>
    /// <param name="BatchWriter">The initial value for the <see cref="BatchWriter"/> property.</param>
    /// <param name="Mutation">The initial value for the <see cref="Mutation"/> property.</param>
    public StateWriteBatchPlan(
        ResourceId ResourceId,
        IResourceBatchWriter BatchWriter,
        ResourceWriteMutation Mutation
    )
    {
        this.ResourceId = ResourceId;
        this.BatchWriter = BatchWriter;
        this.Mutation = Mutation;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="ResourceId">Receives the current <see cref="ResourceId"/> value.</param>
    /// <param name="BatchWriter">Receives the current <see cref="BatchWriter"/> value.</param>
    /// <param name="Mutation">Receives the current <see cref="Mutation"/> value.</param>
    public void Deconstruct(
        out ResourceId ResourceId,
        out IResourceBatchWriter BatchWriter,
        out ResourceWriteMutation Mutation
    )
    {
        ResourceId = this.ResourceId;
        BatchWriter = this.BatchWriter;
        Mutation = this.Mutation;
    }
}

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
