using Configlue.Resources;
using Microsoft.AspNetCore.Http;

namespace Configlue.Hosting.AspNetCore;

/// <summary>Configures paths, operation context, and payload handling for an HTTP resource endpoint.</summary>
/// <remarks>
/// GET accepts standard <c>If-None-Match</c> entity-tag lists. PUT intentionally supports one strong
/// Configlue revision in <c>If-Match</c>, or <c>If-None-Match: *</c>, because resource writes use one
/// compare-and-swap revision rather than a list of alternative revisions.
/// </remarks>
public sealed class HttpResourceEndpointOptions
{
    /// <summary>The endpoint path, relative to the route root, used for GET requests.</summary>
    public string GetPath { get; init; } = "get";

    /// <summary>The endpoint path, relative to the route root, used for PUT requests.</summary>
    public string UpdatePath { get; init; } = "update";

    /// <summary>The media type accepted for writes and returned for successful reads.</summary>
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>Whether the mapped endpoint group requires authorization.</summary>
    public bool RequireAuthorization { get; init; } = true;

    /// <summary>Resolves the resource operation context from trusted server-side request state.</summary>
    /// <remarks>
    /// Use authenticated claims, route authorization, or application-owned state to select a model,
    /// subject, key, and route. Client-supplied context headers must not be treated as placement or
    /// authorization policy. When unset, the endpoint uses <see cref="ConfiglueResourceContext.Default"/>.
    /// </remarks>
    public Func<
        HttpContext,
        CancellationToken,
        ValueTask<ConfiglueResourceContext>
    >? ResourceContextResolver { get; init; }

    /// <summary>The maximum accepted PUT request body size in bytes. Set to <see langword="null"/> to disable the limit.</summary>
    public long? MaximumRequestBodySize { get; init; } = 30_000_000;
}
