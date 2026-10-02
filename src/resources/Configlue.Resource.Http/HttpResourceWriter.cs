namespace Configlue.Resource.Http;

/// <summary>Writes bytes to an HTTP resource. Create this capability only for writable endpoints.</summary>
public sealed class HttpResourceWriter : IResourceWriter, IResourceIdentity
{
    private readonly HttpResourceReader _reader;

    /// <summary>Creates the write capability for an HTTP resource reader.</summary>
    public HttpResourceWriter(HttpResourceReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => _reader.WriteAsync(context, request, cancellationToken);

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _reader.GetResourceId(context);
}
