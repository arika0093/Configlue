using System.Text.Json;
using Configlue.Provider.Json;

namespace Configlue.Resource.Http;

/// <summary>Options for registering a JSON-over-HTTP source through the one-arity facade.</summary>
public sealed class JsonHttpSourceOptions
{
    /// <summary>An optional stable logical source ID used for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The HTTP resource endpoint root.</summary>
    public required string EndPoint { get; init; }

    /// <summary>A directly supplied client; it remains caller-owned.</summary>
    public HttpClient? Client { get; init; }

    /// <summary>Resolves a client when the facade context is created.</summary>
    /// <remarks>The returned client remains owned by the service or factory that supplied it.</remarks>
    public Func<IServiceProvider?, HttpClient>? ClientFactory { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether the endpoint supports writes. Defaults to read-only.</summary>
    public bool Writable { get; init; }

    /// <summary>Whether to poll the endpoint for changes.</summary>
    public bool WatchChanges { get; init; } = true;

    /// <summary>HTTP endpoint paths, content type, and polling interval.</summary>
    public HttpResourceOptions? ResourceOptions { get; init; }

    /// <summary>An optional stable physical resource identity.</summary>
    public ResourceId? ResourceId { get; init; }

    /// <summary>JSON serialization and property naming options.</summary>
    public JsonSerializerOptions? SerializerOptions { get; init; }

    /// <summary>The persisted document structure. Reads accept both layouts; writes use the selected one.</summary>
    public DocumentLayoutOptions? DocumentLayout { get; init; }

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }

    /// <summary>Byte transformers applied when reading and writing this source.</summary>
    public IReadOnlyList<IStateByteTransformer>? Transformers { get; init; }

    internal HttpSourceOptions ToHttpSourceOptions() =>
        new()
        {
            Id = Id,
            EndPoint = EndPoint,
            Client = Client,
            ClientFactory = ClientFactory,
            Codec = new JsonStateCodec(SerializerOptions, DocumentLayout),
            Priority = Priority,
            FallbackCondition = FallbackCondition,
            Writable = Writable,
            WatchChanges = WatchChanges,
            ResourceOptions = ResourceOptions,
            ResourceId = ResourceId,
            CodecContext = CodecContext,
            Transformers = Transformers,
        };
}

/// <summary>Registers facade sources backed by JSON over the Configlue HTTP resource protocol.</summary>
public static class JsonHttpSourceRegistration
{
    /// <summary>Adds a JSON HTTP source. The client remains owned by its provider.</summary>
    public static ConfiglueSourceRegistration FromJsonHttp(
        this ConfiglueSourceSetBuilder sources,
        JsonHttpSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        return sources.FromHttp(options.ToHttpSourceOptions());
    }
}
