namespace Configlue.Resources;

/// <summary>Writes bytes to a physical resource for one logical operation.</summary>
public interface IResourceWriter
{
    /// <summary>Writes the resource, optionally requiring the backend revision to match.</summary>
    ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    );
}
