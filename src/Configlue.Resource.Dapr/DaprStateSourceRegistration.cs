using Dapr.Client;

namespace Configlue.Resource.Dapr;

/// <summary>Options for registering a source backed by one Dapr state store key.</summary>
public sealed class DaprStateSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The Dapr state store name.</summary>
    public required string StoreName { get; init; }

    /// <summary>The state key.</summary>
    public required string Key { get; init; }

    /// <summary>A directly supplied client. It remains caller-owned.</summary>
    public DaprClient? Client { get; init; }

    /// <summary>Resolves a client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, DaprClient>? ClientFactory { get; init; }

    /// <summary>The codec for the serialized state.</summary>
    public required object Codec { get; init; }

    /// <summary>Resource identity, consistency, and metadata settings.</summary>
    public DaprStateResourceOptions? ResourceOptions { get; init; }

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

/// <summary>Registers facade sources backed by Dapr State Management.</summary>
public static class DaprStateSourceRegistration
{
    /// <summary>Adds a Dapr state source. The supplied Dapr client remains externally owned.</summary>
    public static ConfiglueSourceRegistration FromDaprState(
        this ConfiglueSourceSetBuilder sources,
        DaprStateSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StoreName);
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

        return sources.Add(new DaprStateSourceDefinition(options));
    }

    private sealed class DaprStateSourceDefinition(DaprStateSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            return context.Complete(
                CreateSourceCore<TFragment>(context.ModelSchema, context.Services)
            );
        }

        private StateSource<TFragment> CreateSourceCore<TFragment>(
            ConfiglueModelSchema modelSchema,
            IServiceProvider? serviceProvider
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(modelSchema);
            var client = options.Client ?? options.ClientFactory!(serviceProvider);
            if (client is null)
            {
                throw new InvalidOperationException("The Dapr client factory returned null.");
            }

            var resource = new DaprStateResource(
                client,
                options.StoreName,
                options.Key,
                options.ResourceOptions
            );
            var reader = new SerializedStateReader<TFragment>(
                resource,
                options.Codec,
                options.CodecContext
            );
            IStateWriter<TFragment>? writer = options.Writable
                ? new SerializedStateWriter<TFragment>(
                    resource,
                    options.Codec,
                    options.CodecContext
                )
                : null;
            var physicalOrigin = $"dapr:{options.StoreName}";
            return options.Id is { } id
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
        }
    }
}
