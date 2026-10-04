using System.Text.Json;

namespace Configlue.Source.Http;

/// <summary>Options for registering a typed State HTTP source.</summary>
public sealed class HttpStateSourceOptions
{
    /// <summary>An optional stable logical source ID used for provenance and explicit routing.</summary>
    public string? Id { get; set; }

    /// <summary>The State HTTP endpoint root (the GET/PUT URL, without the SSE suffix).</summary>
    public required string EndPoint { get; set; }

    /// <summary>The SSE endpoint. Defaults to <c>{EndPoint}/events</c>.</summary>
    public string? EventsEndPoint { get; set; }

    /// <summary>A directly supplied client; it remains caller-owned.</summary>
    public HttpClient? Client { get; set; }

    /// <summary>Resolves a client at context creation, for example from IHttpClientFactory in DI.</summary>
    /// <remarks>The returned client remains owned by the service or factory that supplied it.</remarks>
    public Func<IServiceProvider?, HttpClient>? ClientFactory { get; set; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; set; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; set; } = StateFallbackCondition.NotFound;

    /// <summary>Whether the endpoint supports writes. Defaults to read-only.</summary>
    public bool Writable { get; set; }

    /// <summary>Whether to watch the SSE endpoint for changes.</summary>
    public bool WatchChanges { get; set; } = true;

    /// <summary>The maximum duration of a GET/PUT request.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The initial SSE reconnect delay.</summary>
    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The maximum SSE reconnect delay (exponential backoff cap).</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// JSON options for fragment serialization. Must agree with the server for ETag stability.
    /// For NativeAOT, supply a source-generated resolver covering scalar/collection member types.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }
}
