using Configlue.Codecs;
using Configlue.Extensibility;
using Configlue.Sources;

namespace Configlue.Resource.Etcd;

/// <summary>Options for registering a multi-key etcd source backed by a key prefix.</summary>
public sealed class EtcdStateSourceOptions
{
    /// <summary>An optional stable logical source ID.</summary>
    public string? Id { get; init; }

    /// <summary>A caller-owned etcd client used for every route.</summary>
    public IEtcdClient? Client { get; init; }

    /// <summary>Creates or resolves a caller-owned etcd client when the source context is created.</summary>
    public Func<IServiceProvider?, IEtcdClient>? ClientFactory { get; init; }

    /// <summary>Resolves a caller-owned etcd client for each physical route.</summary>
    public Func<IServiceProvider?, RouteKey, IEtcdClient>? ClientResolver { get; init; }

    /// <summary>Key prefix, endpoint, and transport settings.</summary>
    public EtcdResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer.</summary>
    public bool Writable { get; init; } = true;

    internal void Validate()
    {
        if (
            (Client is null ? 0 : 1)
                + (ClientFactory is null ? 0 : 1)
                + (ClientResolver is null ? 0 : 1)
            != 1
        )
        {
            throw new ArgumentException(
                "Configure exactly one of Client, ClientFactory, or ClientResolver."
            );
        }

        if (Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        }

        ResourceOptions?.Validate();
    }
}

/// <summary>Options for registering a serialized single-key etcd source.</summary>
public sealed class EtcdObjectSourceOptions
{
    /// <summary>An optional stable logical source ID.</summary>
    public string? Id { get; init; }

    /// <summary>A caller-owned etcd client used for every route.</summary>
    public IEtcdClient? Client { get; init; }

    /// <summary>Creates or resolves a caller-owned etcd client when the source context is created.</summary>
    public Func<IServiceProvider?, IEtcdClient>? ClientFactory { get; init; }

    /// <summary>Resolves a caller-owned etcd client for each physical route.</summary>
    public Func<IServiceProvider?, RouteKey, IEtcdClient>? ClientResolver { get; init; }

    /// <summary>The codec for the serialized state.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Key prefix, endpoint, and transport settings.</summary>
    public EtcdResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer.</summary>
    public bool Writable { get; init; } = true;

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Codec);
        if (
            (Client is null ? 0 : 1)
                + (ClientFactory is null ? 0 : 1)
                + (ClientResolver is null ? 0 : 1)
            != 1
        )
        {
            throw new ArgumentException(
                "Configure exactly one of Client, ClientFactory, or ClientResolver."
            );
        }

        if (Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        }

        ResourceOptions?.Validate();
    }
}

/// <summary>Registers sources backed by etcd v3 key ranges. Supplied clients remain caller-owned.</summary>
public static class EtcdSourceRegistration
{
    /// <summary>Adds a multi-key etcd source. Supplied clients remain externally owned.</summary>
    /// <param name="sources">The source set builder.</param>
    /// <param name="options">The etcd source options.</param>
    public static ConfiglueSourceRegistration FromEtcd(
        this ConfiglueSourceSetBuilder sources,
        EtcdStateSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new EtcdStateSourceDefinition(options)
        );
    }

    /// <summary>Adds a serialized single-key etcd source. Supplied clients remain externally owned.</summary>
    /// <param name="sources">The source set builder.</param>
    /// <param name="options">The etcd object source options.</param>
    public static ConfiglueSourceRegistration FromEtcdObject(
        this ConfiglueSourceSetBuilder sources,
        EtcdObjectSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new EtcdObjectSourceDefinition(options)
        );
    }

    private sealed class EtcdStateSourceDefinition(EtcdStateSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            EtcdSource<TFragment> source;
            if (options.Client is { } client)
            {
                source = new EtcdSource<TFragment>(
                    context.ModelSchema,
                    client,
                    options.ResourceOptions,
                    options.Writable
                );
            }
            else if (options.ClientFactory is { } factory)
            {
                var resolved =
                    factory(context.Services)
                    ?? throw new InvalidOperationException(
                        "The etcd client factory returned null."
                    );
                source = new EtcdSource<TFragment>(
                    context.ModelSchema,
                    resolved,
                    options.ResourceOptions,
                    options.Writable
                );
            }
            else
            {
                var resolver = options.ClientResolver!;
                source = new EtcdSource<TFragment>(
                    context.ModelSchema,
                    route =>
                        resolver(context.Services, route)
                        ?? throw new InvalidOperationException(
                            "The etcd client resolver returned null."
                        ),
                    options.ResourceOptions,
                    options.Writable
                );
            }

            context.Own(source);
            var physicalOrigin = $"etcd:{options.ResourceOptions?.KeyPrefix ?? "configlue"}";
            StateSource<TFragment> stateSource = ConfiglueSourceCompletion.WithDescribedIdentity(
                source,
                options.Id,
                null,
                options.Priority,
                options.FallbackCondition,
                physicalOrigin,
                options.ResourceOptions?.FixedResourceId
            );
            return context.Complete(stateSource);
        }
    }

    private sealed class EtcdObjectSourceDefinition(EtcdObjectSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            EtcdResource resource;
            if (options.Client is { } client)
            {
                resource = new EtcdResource(client, options.ResourceOptions);
            }
            else if (options.ClientFactory is { } factory)
            {
                var resolved =
                    factory(context.Services)
                    ?? throw new InvalidOperationException(
                        "The etcd client factory returned null."
                    );
                resource = new EtcdResource(resolved, options.ResourceOptions);
            }
            else
            {
                var resolver = options.ClientResolver!;
                resource = new EtcdResource(
                    route =>
                        resolver(context.Services, route)
                        ?? throw new InvalidOperationException(
                            "The etcd client resolver returned null."
                        ),
                    options.ResourceOptions
                );
            }

            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                writer: options.Writable ? resource : null
            );
            var physicalOrigin = $"etcd:{options.ResourceOptions?.KeyPrefix ?? "configlue"}";
            StateSource<TFragment> stateSource = ConfiglueSourceCompletion.WithDescribedIdentity(
                serialized,
                options.Id,
                string.Join(
                    "\n",
                    options.ResourceOptions?.KeyPrefix,
                    options.ResourceOptions?.KeyPrefixSelector?.Method.ToString(),
                    string.Join(
                        ",",
                        options.ResourceOptions?.Endpoints ?? ["http://127.0.0.1:2379"]
                    )
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
