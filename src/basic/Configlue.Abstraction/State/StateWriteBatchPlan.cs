using Configlue.Resources;

namespace Configlue.State;

/// <summary>A prepared typed source mutation for a batch-capable physical resource.</summary>
public sealed record StateWriteBatchPlan
{
    /// <summary>Gets or initializes the <see cref="ResourceId"/> value.</summary>
    public ResourceId ResourceId { get; }

    /// <summary>Gets or initializes the <see cref="BatchWriter"/> value.</summary>
    public IResourceBatchWriter BatchWriter { get; }

    /// <summary>Gets or initializes the <see cref="Mutation"/> value.</summary>
    public ResourceWriteMutation Mutation { get; }

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
        if (ResourceId.IsDefault)
        {
            throw new ArgumentException(
                "A batch plan requires a non-default resource identity.",
                nameof(ResourceId)
            );
        }
        ArgumentNullException.ThrowIfNull(BatchWriter);
        ArgumentNullException.ThrowIfNull(Mutation);
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
