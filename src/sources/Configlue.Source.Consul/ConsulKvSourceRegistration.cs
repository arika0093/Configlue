using Configlue.Codecs;
using Configlue.Sources;

namespace Configlue.Source.Consul;

/// <summary>Options for registering a Consul KV prefix source.</summary>
public sealed class ConsulKvPrefixSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The Consul key prefix mapping to one model contribution.</summary>
    public required string KeyPrefix { get; init; }

    /// <summary>A directly supplied client. It remains caller-owned.</summary>
    public IConsulKvClient? Client { get; init; }

    /// <summary>Resolves a client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, IConsulKvClient>? ClientFactory { get; init; }

    /// <summary>Resolves a caller-owned, shared client for each physical route.</summary>
    public Func<IServiceProvider?, RouteKey, IConsulKvClient>? ClientResolver { get; init; }

    /// <summary>Prefix, datacenter, namespace, and consistency settings.</summary>
    public ConsulKvPrefixOptions? PrefixOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer. Defaults to true.</summary>
    public bool Writable { get; init; } = true;
}

/// <summary>Options for registering a single-key Consul object source via the codec pipeline.</summary>
public sealed class ConsulKvObjectSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The single Consul key holding the serialized payload.</summary>
    public required string Key { get; init; }

    /// <summary>A directly supplied client. It remains caller-owned.</summary>
    public IConsulKvClient? Client { get; init; }

    /// <summary>Resolves a client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, IConsulKvClient>? ClientFactory { get; init; }

    /// <summary>The codec for the serialized object.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Key, datacenter, and identity settings.</summary>
    public ConsulKvResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer. Defaults to true.</summary>
    public bool Writable { get; init; } = true;

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }
}

/// <summary>Registers Consul KV sources.</summary>
public static class ConsulKvSourceRegistration
{
    /// <summary>Adds a Consul KV prefix source. Supplied clients remain externally owned.</summary>
    public static ConfiglueSourceRegistration FromConsul(
        this ConfiglueSourceSetBuilder sources,
        ConsulKvPrefixSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.KeyPrefix);
        options.PrefixOptions?.Validate();
        if (
            (options.Client is null ? 0 : 1)
                + (options.ClientFactory is null ? 0 : 1)
                + (options.ClientResolver is null ? 0 : 1)
            != 1
        )
        {
            throw new ArgumentException(
                "Configure exactly one of Client, ClientFactory, or ClientResolver.",
                nameof(options)
            );
        }

        if (options.Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new ConsulPrefixSourceDefinition(options)
        );
    }

    /// <summary>
    /// Adds a single-key Consul object source through the normal Resource + Codec pipeline.
    /// Supplied clients remain externally owned.
    /// </summary>
    public static ConfiglueSourceRegistration FromConsulObject(
        this ConfiglueSourceSetBuilder sources,
        ConsulKvObjectSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Key);
        ArgumentNullException.ThrowIfNull(options.Codec);
        if ((options.Client is null) == (options.ClientFactory is null))
        {
            throw new ArgumentException(
                "Configure exactly one of Client or ClientFactory.",
                nameof(options)
            );
        }

        if (options.Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new ConsulObjectSourceDefinition(options)
        );
    }

    private sealed class ConsulPrefixSourceDefinition(ConsulKvPrefixSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var schema = context.ModelSchema;
            ConsulKvSource<TFragment> source;
            if (options.ClientResolver is { } resolver)
            {
                source = new ConsulKvSource<TFragment>(
                    route =>
                        resolver(context.Services, route)
                        ?? throw new InvalidOperationException(
                            "The Consul client resolver returned null."
                        ),
                    schema,
                    options.KeyPrefix,
                    options.PrefixOptions,
                    options.Writable
                );
            }
            else
            {
                var client =
                    options.Client
                    ?? options.ClientFactory!(context.Services)
                    ?? throw new InvalidOperationException(
                        "The Consul client factory returned null."
                    );
                source = new ConsulKvSource<TFragment>(
                    client,
                    schema,
                    options.KeyPrefix,
                    options.PrefixOptions,
                    options.Writable
                );
            }

            context.Own(source);
            var physicalOrigin =
                $"consul:{ConsulKeyNormalization.NormalizePrefix(options.KeyPrefix)}";
            var stateSource = ConfiglueSourceCompletion.WithDescribedIdentity(
                source,
                options.Id,
                string.Join(
                    "\n",
                    options.KeyPrefix,
                    options.PrefixOptions?.Datacenter,
                    options.PrefixOptions?.Namespace,
                    options.PrefixOptions?.Partition,
                    options.PrefixOptions?.Consistency.ToString()
                ),
                options.Priority,
                options.FallbackCondition,
                physicalOrigin,
                options.PrefixOptions?.FixedResourceId
            );
            return context.Complete(stateSource);
        }
    }

    private sealed class ConsulObjectSourceDefinition(ConsulKvObjectSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var client =
                options.Client
                ?? options.ClientFactory!(context.Services)
                ?? throw new InvalidOperationException("The Consul client factory returned null.");
            var resource = new ConsulKvResource(client, options.Key, options.ResourceOptions);
            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                writer: options.Writable ? resource : null,
                watcher: resource
            );
            var physicalOrigin = $"consul:{ConsulKeyNormalization.NormalizeKey(options.Key)}";
            var stateSource = ConfiglueSourceCompletion.WithDescribedIdentity(
                serialized,
                options.Id,
                string.Join(
                    "\n",
                    options.Key,
                    options.ResourceOptions?.Datacenter,
                    options.ResourceOptions?.Namespace,
                    options.ResourceOptions?.Partition
                ),
                options.Priority,
                options.FallbackCondition,
                physicalOrigin,
                options.ResourceOptions?.FixedResourceId
            );
            return context.Complete(stateSource);
        }
    }
}
