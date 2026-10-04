namespace Configlue.Resources;

/// <summary>Writes bytes to a physical resource for one logical operation.</summary>
/// <remarks>Provider SPI: implemented by resource authors, consumed by the runtime.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IResourceWriter
{
    /// <summary>Writes the resource, optionally requiring the backend revision to match.</summary>
    ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    );
}
