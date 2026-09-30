namespace Configlue.Resources;

/// <summary>Reads bytes and backend metadata from a physical resource for one logical operation.</summary>
public interface IResourceReader
{
    /// <summary>Reads the resource for one model, subject, source-specific key, and route.</summary>
    ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    );
}
