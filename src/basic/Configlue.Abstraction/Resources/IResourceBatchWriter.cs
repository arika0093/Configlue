namespace Configlue.Resources;

/// <summary>Persists several disjoint logical resource mutations with one physical resource write.</summary>
/// <remarks>
/// A batch writer's <see cref="IResourceIdentity.GetResourceId(ConfiglueResourceContext)"/> declares its physical coordination
/// domain. Several writers sharing one <see cref="ResourceId"/> are combined into a single physical
/// write only when they are the same object: the runtime executes the group through the first writer,
/// so distinct instances are rejected before any write even when they report the same identity.
/// Providers that need atomic multi-mutation writes must therefore expose one canonical batch writer
/// object per physical resource (section and ZIP views forward their underlying physical writer;
/// transforming wrappers require sharing the same wrapper writer object).
/// Before writing, validate mutations with <see cref="ResourceWriteMutation.ValidateBatch"/> and
/// resolve physical schema metadata with <see cref="ResourceWriteMutation.ResolveBatchSchema"/>.
/// A null resolved schema makes no metadata claim; preserve existing container metadata when
/// the resource stores it. Conflicting schema declarations must be rejected before writing.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IResourceBatchWriter : IResourceWriter, IResourceIdentity
{
    /// <summary>Applies the mutations in order and persists the resulting resource once.</summary>
    ValueTask<StateWriteResult> WriteBatchAsync(
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Prepares a logical resource view update for its underlying batch-capable physical resource.</summary>
/// <remarks>Advanced provider SPI for batch-capable resources.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IResourceBatchParticipant
{
    /// <summary>The physical writer that can combine this update with other disjoint mutations.</summary>
    IResourceBatchWriter? BatchWriter { get; }

    /// <summary>Gets the physical resource receiving a mutation for one operation context.</summary>
    ResourceId GetResourceId(ConfiglueResourceContext context);

    /// <summary>Creates a deferred mutation from a logical resource write request.</summary>
    ResourceWriteMutation CreateMutation(
        ConfiglueResourceContext context,
        ResourceWriteRequest request
    );
}
