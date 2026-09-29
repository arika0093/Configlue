namespace Configlue.Resources;

/// <summary>Reads bytes and backend metadata from a physical resource.</summary>
public interface IResourceReader
{
    /// <summary>Reads the resource.</summary>
    ValueTask<ResourceReadResult> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the resource for one logical subject and source-specific key.</summary>
    ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    ) => ReadAsync(cancellationToken);
}
