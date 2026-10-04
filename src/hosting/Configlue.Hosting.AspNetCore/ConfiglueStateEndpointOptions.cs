using System.Text.Json;

namespace Configlue.Hosting.AspNetCore;

/// <summary>Configures the typed State HTTP endpoints for one Configlue model.</summary>
/// <remarks>
/// Authorization composes with normal ASP.NET Core endpoint conventions: call
/// <c>RequireAuthorization()</c> on the returned group instead of configuring
/// authorization here.
/// </remarks>
public sealed class ConfiglueStateEndpointOptions
{
    /// <summary>Whether to map the GET state endpoint.</summary>
    public bool MapRead { get; init; } = true;

    /// <summary>Whether to map the PUT state endpoint.</summary>
    public bool MapWrite { get; init; } = true;

    /// <summary>
    /// Whether to map the RFC 6902 JSON Patch endpoint (<c>application/json-patch+json</c>).
    /// The patch endpoint shares the write path and requires a strong effective-state
    /// <c>If-Match</c> ETag.
    /// </summary>
    public bool MapPatch { get; init; } = true;

    /// <summary>Whether to map the SSE invalidation endpoint.</summary>
    public bool MapEvents { get; init; } = true;

    /// <summary>The read path relative to the route pattern. Empty maps at the pattern root.</summary>
    public string ReadPath { get; init; } = "";

    /// <summary>The write path relative to the route pattern. Empty maps at the pattern root.</summary>
    public string WritePath { get; init; } = "";

    /// <summary>The SSE path relative to the route pattern.</summary>
    public string EventsPath { get; init; } = "events";

    /// <summary>
    /// JSON options for fragment serialization. The resolver must provide metadata for scalar
    /// and collection member types delegated by the generated fragment converter. For NativeAOT,
    /// supply a source-generated <c>JsonSerializerContext</c>.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; init; }

    /// <summary>The maximum accepted PUT/PATCH request body size in bytes. Null disables the limit.</summary>
    public long? MaximumRequestBodySize { get; init; } = 30_000_000;
}
