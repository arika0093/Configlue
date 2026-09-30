namespace Configlue.Resources;

/// <summary>Writes bytes to a physical resource.</summary>
public interface IResourceWriter
{
    /// <summary>Writes the resource, optionally requiring the backend revision to match.</summary>
    ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Writes a physical resource for one logical subject and source-specific key.</summary>
public interface IContextualResourceWriter : IResourceWriter
{
    /// <summary>Writes the resource for one logical subject and source-specific key.</summary>
    ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    );
}
