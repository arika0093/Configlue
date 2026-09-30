namespace Configlue.Resource.Http;

/// <summary>Configures the endpoints and polling interval for an HTTP resource.</summary>
public sealed class HttpResourceOptions
{
    /// <summary>Resolves an endpoint root for subject-aware reads, writes, and watches.</summary>
    /// <remarks>
    /// Return a stable root for a given context. When unset, every subject uses the endpoint root
    /// supplied to the resource.
    /// </remarks>
    public Func<ConfiglueResourceContext, Uri>? EndpointRootSelector { get; init; }

    /// <summary>The endpoint path, relative to the resource root, used for GET requests.</summary>
    public string GetPath { get; init; } = "get";

    /// <summary>The endpoint path, relative to the resource root, used for PUT requests.</summary>
    public string UpdatePath { get; init; } = "update";

    /// <summary>The content type sent with resource writes.</summary>
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>The interval used when polling for resource changes.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The maximum delay between polls while the HTTP resource is unavailable.</summary>
    public TimeSpan MaximumPollingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The maximum duration of an HTTP request made by the resource.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
