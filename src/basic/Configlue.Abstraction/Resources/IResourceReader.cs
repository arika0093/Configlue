namespace Configlue.Resources;

/// <summary>Reads bytes and backend metadata from a physical resource for one logical operation.</summary>
/// <remarks>Provider SPI: implemented by resource authors, consumed by the runtime.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IResourceReader
{
    /// <summary>Reads the resource for one model, subject, source-specific key, and route.</summary>
    ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    );
}
