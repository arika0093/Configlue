namespace Configlue.Resource.Http;

/// <summary>Writes bytes to an HTTP resource. Create this capability only for writable endpoints.</summary>
public sealed class HttpResourceWriter : IResourceWriter, ITryContextualResourceIdentity
{
    private readonly HttpResourceReader _reader;

    /// <summary>Creates the write capability for an HTTP resource reader.</summary>
    public HttpResourceWriter(HttpResourceReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <inheritdoc />
    public ResourceId ResourceId => _reader.ResourceId;

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => _reader.WriteAsync(context, request, cancellationToken);

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _reader.GetResourceId(context);

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId) =>
        _reader.TryGetResourceId(context, out resourceId);
}
