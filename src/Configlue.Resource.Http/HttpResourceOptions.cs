namespace Configlue.Resource.Http;

/// <summary>Configures the endpoints and polling interval for an HTTP resource.</summary>
public sealed class HttpResourceOptions
{
    /// <summary>The endpoint path, relative to the resource root, used for GET requests.</summary>
    public string GetPath { get; init; } = "get";

    /// <summary>The endpoint path, relative to the resource root, used for PUT requests.</summary>
    public string UpdatePath { get; init; } = "update";

    /// <summary>The content type sent with resource writes.</summary>
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>The interval used when polling for resource changes.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(5);
}
