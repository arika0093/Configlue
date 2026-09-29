namespace Configlue.Resources;

/// <summary>Persists several disjoint logical resource mutations with one physical resource write.</summary>
public interface IResourceBatchWriter : IResourceWriter, IResourceIdentity
{
    /// <summary>Applies the mutations in order and persists the resulting resource once.</summary>
    ValueTask<StateWriteResult> WriteBatchAsync(
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Prepares a logical resource view update for its underlying batch-capable physical resource.</summary>
public interface IResourceBatchParticipant
{
    /// <summary>The physical resource receiving the prepared update.</summary>
    ResourceId ResourceId { get; }

    /// <summary>The physical resource receiving a mutation for one subject.</summary>
    ResourceId GetResourceId(ConfiglueResourceContext context) => ResourceId;

    /// <summary>The physical writer that can combine this update with other disjoint mutations.</summary>
    IResourceBatchWriter? BatchWriter { get; }

    /// <summary>Creates a deferred mutation from a logical resource write request.</summary>
    ResourceWriteMutation CreateMutation(ResourceWriteRequest request);

    /// <summary>Creates a deferred mutation for one logical subject.</summary>
    ResourceWriteMutation CreateMutation(
        ConfiglueResourceContext context,
        ResourceWriteRequest request
    ) => CreateMutation(request).WithContext(context);
}
