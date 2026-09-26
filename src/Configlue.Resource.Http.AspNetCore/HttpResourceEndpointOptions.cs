namespace Configlue.Resource.Http.AspNetCore;

/// <summary>Configures paths and the payload media type for an HTTP resource endpoint.</summary>
public sealed class HttpResourceEndpointOptions
{
    /// <summary>The endpoint path, relative to the route root, used for GET requests.</summary>
    public string GetPath { get; init; } = "get";

    /// <summary>The endpoint path, relative to the route root, used for PUT requests.</summary>
    public string UpdatePath { get; init; } = "update";

    /// <summary>The media type accepted for writes and returned for successful reads.</summary>
    public string ContentType { get; init; } = "application/octet-stream";
}
