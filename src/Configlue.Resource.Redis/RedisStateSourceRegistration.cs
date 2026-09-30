using Configlue.Sources;
using StackExchange.Redis;

namespace Configlue.Resource.Redis;

/// <summary>Options for registering a Redis-backed serialized state source.</summary>
public sealed class RedisStateSourceOptions
{
    /// <summary>An optional stable logical source ID.</summary>
    public string? Id { get; init; }

    /// <summary>The namespace separating this resource's Redis rows.</summary>
    public required string ResourceNamespace { get; init; }

    /// <summary>A caller-owned multiplexer used for every route.</summary>
    public IConnectionMultiplexer? ConnectionMultiplexer { get; init; }

    /// <summary>Creates or resolves a caller-owned multiplexer when the source context is created.</summary>
    public Func<
        IServiceProvider?,
        IConnectionMultiplexer
    >? ConnectionMultiplexerFactory { get; init; }

    /// <summary>Resolves a caller-owned, shared multiplexer for each physical route.</summary>
    public Func<
        IServiceProvider?,
        RouteKey,
        IConnectionMultiplexer
    >? ConnectionMultiplexerResolver { get; init; }

    /// <summary>The codec for the serialized state.</summary>
    public required object Codec { get; init; }

    /// <summary>Key, database, and resource identity settings.</summary>
    public RedisResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer.</summary>
    public bool Writable { get; init; } = true;

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }
}

/// <summary>Registers sources backed by Redis byte resources.</summary>
public static class RedisStateSourceRegistration
{
    /// <summary>Adds a Redis source. Supplied multiplexers remain externally owned.</summary>
    public static ConfiglueSourceRegistration FromRedis(
        this ConfiglueSourceSetBuilder sources,
        RedisStateSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ResourceNamespace);
        if (options.ResourceNamespace.Contains('\0'))
        {
            throw new ArgumentException(
                "A Redis string namespace cannot contain NUL.",
                nameof(options)
            );
        }

        ArgumentNullException.ThrowIfNull(options.Codec);
        options.ResourceOptions?.Validate();
        if (
            (options.ConnectionMultiplexer is null ? 0 : 1)
                + (options.ConnectionMultiplexerFactory is null ? 0 : 1)
                + (options.ConnectionMultiplexerResolver is null ? 0 : 1)
            != 1
        )
        {
            throw new ArgumentException(
                "Configure exactly one of ConnectionMultiplexer, ConnectionMultiplexerFactory, or ConnectionMultiplexerResolver.",
                nameof(options)
            );
        }

        if (options.Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new RedisStateSourceDefinition(options)
        );
    }

    private sealed class RedisStateSourceDefinition(RedisStateSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var fixedMultiplexer = options.ConnectionMultiplexer;
            if (options.ConnectionMultiplexerFactory is { } multiplexerFactory)
            {
                fixedMultiplexer =
                    multiplexerFactory(context.Services)
                    ?? throw new InvalidOperationException(
                        "The Redis connection multiplexer factory returned null."
                    );
            }

            var resource = options.ConnectionMultiplexerResolver is { } multiplexerResolver
                ? new RedisResource(
                    route =>
                        multiplexerResolver(context.Services, route)
                        ?? throw new InvalidOperationException(
                            "The Redis connection multiplexer resolver returned null."
                        ),
                    options.ResourceNamespace,
                    options.ResourceOptions
                )
                : new RedisResource(
                    fixedMultiplexer!,
                    options.ResourceNamespace,
                    options.ResourceOptions
                );
            context.Own(resource);

            var reader = new SerializedStateReader<TFragment>(
                resource,
                options.Codec,
                options.CodecContext
            );
            ISourceWriter<TFragment>? writer = options.Writable
                ? new SerializedStateWriter<TFragment>(
                    resource,
                    options.Codec,
                    options.CodecContext
                )
                : null;
            var physicalOrigin = $"redis:{options.ResourceNamespace}";
            var source = options.Id is { } id
                ? new StateSource<TFragment>(
                    id,
                    reader,
                    options.Priority,
                    options.FallbackCondition,
                    writer,
                    physicalOrigin: physicalOrigin,
                    resourceId: options.ResourceOptions?.ResourceId
                )
                : new StateSource<TFragment>(
                    reader,
                    options.Priority,
                    options.FallbackCondition,
                    writer,
                    physicalOrigin: physicalOrigin,
                    resourceId: options.ResourceOptions?.ResourceId
                );
            return context.Complete(source);
        }
    }
}
