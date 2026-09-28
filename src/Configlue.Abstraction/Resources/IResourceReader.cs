namespace Configlue.Resources;

/// <summary>Reads bytes and backend metadata from a physical resource.</summary>
public interface IResourceReader
{
    /// <summary>Reads the resource.</summary>
    ValueTask<ResourceReadResult> ReadAsync(CancellationToken cancellationToken = default);
}
