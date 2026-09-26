namespace Configlue;

/// <summary>Writes bytes to a physical resource.</summary>
public interface IResourceWriter
{
    /// <summary>Writes the resource, optionally requiring the backend revision to match.</summary>
    ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    );
}
