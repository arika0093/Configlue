namespace Configlue.Resource.Http;

/// <summary>Options for registering an HTTP-backed state source through the one-arity facade.</summary>
public sealed class HttpSourceOptions
{
    /// <summary>An optional stable logical source ID used for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The HTTP resource endpoint root.</summary>
    public required string EndPoint { get; init; }

    /// <summary>A directly supplied client; it remains caller-owned.</summary>
    public HttpClient? Client { get; init; }

    /// <summary>Resolves a client at context creation, for example from IHttpClientFactory in DI.</summary>
    /// <remarks>The returned client remains owned by the service or factory that supplied it.</remarks>
    public Func<IServiceProvider?, HttpClient>? ClientFactory { get; init; }

    /// <summary>The codec for the serialized state.</summary>
    public required object Codec { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether the endpoint supports writes.</summary>
    public bool Writable { get; init; }

    /// <summary>Whether to poll the endpoint for changes.</summary>
    public bool WatchChanges { get; init; } = true;

    /// <summary>HTTP endpoint paths, content type, and polling interval.</summary>
    public HttpResourceOptions? ResourceOptions { get; init; }

    /// <summary>An optional stable physical resource identity.</summary>
    public ResourceId? ResourceId { get; init; }

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }

    /// <summary>Byte transformers applied when reading and writing this source.</summary>
    public IReadOnlyList<IStateByteTransformer>? Transformers { get; init; }
}

/// <summary>Registers facade sources backed by the Configlue HTTP resource protocol.</summary>
public static class HttpSourceRegistration
{
    /// <summary>Adds an HTTP source. Clients and factory-provided handlers remain externally owned.</summary>
    public static ConfiglueSourceRegistration FromHttp(
        this ConfiglueSourceSetBuilder sources,
        HttpSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EndPoint);
        if (!Uri.TryCreate(options.EndPoint, UriKind.Absolute, out var endpoint))
        {
            throw new ArgumentException("EndPoint must be an absolute URI.", nameof(options));
        }

        if (
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new ArgumentException("EndPoint must use HTTP or HTTPS.", nameof(options));
        }
        ArgumentNullException.ThrowIfNull(options.Codec);
        if (options.Client is not null && options.ClientFactory is not null)
        {
            throw new ArgumentException(
                "Configure either Client or ClientFactory, not both.",
                nameof(options)
            );
        }

        if (options.Client is null && options.ClientFactory is null)
        {
            throw new ArgumentException(
                "Configure a direct client or a provider-aware client factory.",
                nameof(options)
            );
        }

        return sources.Add(new HttpSourceDefinition(options));
    }

    private sealed class HttpSourceDefinition(HttpSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema modelSchema,
            IServiceProvider? serviceProvider,
            Action<IDisposable> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            var endpoint = new Uri(options.EndPoint, UriKind.Absolute);
            var client = options.Client ?? options.ClientFactory!(serviceProvider);
            if (client is null)
            {
                throw new InvalidOperationException("The HTTP client factory returned null.");
            }
            var resource = new HttpResourceReader(
                client,
                endpoint,
                options.ResourceOptions,
                options.ResourceId
            );
            IStateWriter<TFragment>? writer = options.Writable
                ? new SerializedStateWriter<TFragment>(
                    resource.CreateWriter(),
                    options.Codec,
                    options.CodecContext,
                    options.Transformers
                )
                : null;
            var reader = new SerializedStateReader<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                transformers: options.Transformers
            );
            var resourceId = options.ResourceId ?? resource.ResourceId;
            var watcher = options.WatchChanges ? resource : null;
            return options.Id is { } id
                ? new StateSource<TFragment>(
                    id,
                    reader,
                    options.Priority,
                    options.FallbackCondition,
                    writer,
                    watcher,
                    endpoint.AbsoluteUri,
                    resourceId
                )
                : new StateSource<TFragment>(
                    reader,
                    options.Priority,
                    options.FallbackCondition,
                    writer,
                    watcher,
                    endpoint.AbsoluteUri,
                    resourceId
                );
        }
    }
}
