using Amazon.AppConfigData;
using Configlue.Codecs;
using Configlue.CompilerServices;
using Configlue.Sources;

namespace Configlue.Resource.AwsAppConfig;

/// <summary>Options for registering a read-only source backed by the AppConfig Data API.</summary>
public sealed class AwsAppConfigSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The AppConfig application identifier or name.</summary>
    public required string ApplicationId { get; init; }

    /// <summary>The AppConfig environment identifier or name.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The AppConfig configuration profile identifier or name.</summary>
    public required string ConfigurationProfileId { get; init; }

    /// <summary>A directly supplied SDK client. It remains caller-owned.</summary>
    public IAmazonAppConfigData? Client { get; init; }

    /// <summary>
    /// Resolves an SDK client at context creation, for example from dependency injection.
    /// When neither <see cref="Client"/> nor <see cref="ClientFactory"/> is configured, a
    /// default client using standard AWS credential and region resolution is created and
    /// owned by the resource.
    /// </summary>
    public Func<IServiceProvider?, IAmazonAppConfigData>? ClientFactory { get; init; }

    /// <summary>The codec for the serialized configuration profile payload.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Client identity, session, and resource identity settings.</summary>
    public AwsAppConfigResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFoundOrUnavailable;

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }
}

/// <summary>Options for registering a read-only source backed by the local AppConfig Agent.</summary>
public sealed class AwsAppConfigAgentSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The AppConfig application name tracked by the agent.</summary>
    public required string ApplicationId { get; init; }

    /// <summary>The AppConfig environment name tracked by the agent.</summary>
    public required string EnvironmentId { get; init; }

    /// <summary>The AppConfig configuration profile name tracked by the agent.</summary>
    public required string ConfigurationProfileId { get; init; }

    /// <summary>A directly supplied HTTP client. It remains caller-owned.</summary>
    public HttpClient? Client { get; init; }

    /// <summary>
    /// Resolves an HTTP client at context creation, for example from
    /// <c>IHttpClientFactory</c> in dependency injection. When neither <see cref="Client"/>
    /// nor <see cref="ClientFactory"/> is configured, a default client is created and owned
    /// by the resource.
    /// </summary>
    public Func<IServiceProvider?, HttpClient>? ClientFactory { get; init; }

    /// <summary>The codec for the serialized configuration profile payload.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Client identity, agent endpoint, poll interval, and identity settings.</summary>
    public AwsAppConfigResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFoundOrUnavailable;

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }
}

/// <summary>Registers read-only facade sources backed by AWS AppConfig.</summary>
public static class AwsAppConfigSourceRegistration
{
    /// <summary>
    /// Adds an AppConfig Data API source. The session follows server-provided tokens and poll
    /// intervals; no-change responses never invalidate loaded state.
    /// </summary>
    public static ConfiglueSourceRegistration FromAwsAppConfig(
        this ConfiglueSourceSetBuilder sources,
        AwsAppConfigSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EnvironmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConfigurationProfileId);
        ArgumentNullException.ThrowIfNull(options.Codec);
        options.ResourceOptions?.Validate(agentMode: false);
        if (options.Client is not null && options.ClientFactory is not null)
        {
            throw new ArgumentException(
                "Configure either Client or ClientFactory, not both.",
                nameof(options)
            );
        }

        if (options.Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new AwsAppConfigSourceDefinition(options)
        );
    }

    /// <summary>
    /// Adds an AppConfig Agent source with identical Configlue semantics to the direct mode.
    /// </summary>
    public static ConfiglueSourceRegistration FromAwsAppConfigAgent(
        this ConfiglueSourceSetBuilder sources,
        AwsAppConfigAgentSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EnvironmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConfigurationProfileId);
        ArgumentNullException.ThrowIfNull(options.Codec);
        options.ResourceOptions?.Validate(agentMode: true);
        if (options.Client is not null && options.ClientFactory is not null)
        {
            throw new ArgumentException(
                "Configure either Client or ClientFactory, not both.",
                nameof(options)
            );
        }

        if (options.Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new AwsAppConfigAgentSourceDefinition(options)
        );
    }

    /// <summary>Registers an AppConfig Data API source as the model's source.</summary>
    public static void UseAwsAppConfig<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        AwsAppConfigSourceOptions options,
        Action<AwsAppConfigSourceOptions>? configure = null
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        configure?.Invoke(options);
        model.Sources(sources => sources.FromAwsAppConfig(options));
    }

    /// <summary>Registers an AppConfig Agent source as the model's source.</summary>
    public static void UseAwsAppConfigAgent<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        AwsAppConfigAgentSourceOptions options,
        Action<AwsAppConfigAgentSourceOptions>? configure = null
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        configure?.Invoke(options);
        model.Sources(sources => sources.FromAwsAppConfigAgent(options));
    }

    private sealed class AwsAppConfigSourceDefinition(AwsAppConfigSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var client = options.Client ?? options.ClientFactory?.Invoke(context.Services);
            if (options.Client is null && options.ClientFactory is not null && client is null)
            {
                throw new InvalidOperationException("The AppConfig client factory returned null.");
            }

            AwsAppConfigResource resource;
            if (client is not null)
            {
                resource = new AwsAppConfigResource(
                    new AwsAppConfigDataSdkClient(client),
                    options.ApplicationId,
                    options.EnvironmentId,
                    options.ConfigurationProfileId,
                    options.ResourceOptions
                );
            }
            else
            {
                // Standard AWS credential and region resolution applies.
                var defaultClient = new AmazonAppConfigDataClient(new AmazonAppConfigDataConfig());
                resource = new AwsAppConfigResource(
                    new AwsAppConfigDataSdkClient(defaultClient),
                    options.ApplicationId,
                    options.EnvironmentId,
                    options.ConfigurationProfileId,
                    options.ResourceOptions,
                    ownedClient: defaultClient
                );
            }

            context.Own(resource);
            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                watcher: resource
            );
            return context.Complete(
                CreateStateSource<TFragment>(
                    serialized,
                    options.Id,
                    $"appconfig:{options.ApplicationId}/{options.EnvironmentId}/{options.ConfigurationProfileId}",
                    options.Priority,
                    options.FallbackCondition,
                    options.ResourceOptions?.FixedResourceId,
                    string.Join(
                        "\n",
                        options.ApplicationId,
                        options.EnvironmentId,
                        options.ConfigurationProfileId,
                        options.ResourceOptions?.ClientId
                    )
                )
            );
        }
    }

    private sealed class AwsAppConfigAgentSourceDefinition(AwsAppConfigAgentSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var client = options.Client ?? options.ClientFactory?.Invoke(context.Services);
            if (options.Client is null && options.ClientFactory is not null && client is null)
            {
                throw new InvalidOperationException("The HTTP client factory returned null.");
            }

            AwsAppConfigResource resource;
            if (client is not null)
            {
                resource = new AwsAppConfigResource(
                    AwsAppConfigResource.CreateAgentFetcher(
                        client,
                        options.ApplicationId,
                        options.EnvironmentId,
                        options.ConfigurationProfileId,
                        options.ResourceOptions
                    ),
                    options.ApplicationId,
                    options.EnvironmentId,
                    options.ConfigurationProfileId,
                    options.ResourceOptions
                );
            }
            else
            {
                var ownedHttpClient = new HttpClient();
                resource = new AwsAppConfigResource(
                    AwsAppConfigResource.CreateAgentFetcher(
                        ownedHttpClient,
                        options.ApplicationId,
                        options.EnvironmentId,
                        options.ConfigurationProfileId,
                        options.ResourceOptions
                    ),
                    options.ApplicationId,
                    options.EnvironmentId,
                    options.ConfigurationProfileId,
                    options.ResourceOptions,
                    ownedClient: ownedHttpClient
                );
            }

            context.Own(resource);
            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                watcher: resource
            );
            return context.Complete(
                CreateStateSource<TFragment>(
                    serialized,
                    options.Id,
                    $"appconfig-agent:{options.ApplicationId}/{options.EnvironmentId}/{options.ConfigurationProfileId}",
                    options.Priority,
                    options.FallbackCondition,
                    options.ResourceOptions?.FixedResourceId,
                    string.Join(
                        "\n",
                        options.ApplicationId,
                        options.EnvironmentId,
                        options.ConfigurationProfileId,
                        options.ResourceOptions?.ClientId
                    )
                )
            );
        }
    }

    private static StateSource<TFragment> CreateStateSource<TFragment>(
        SerializedSource<TFragment> serialized,
        string? id,
        string physicalOrigin,
        int priority,
        StateFallbackCondition fallbackCondition,
        ResourceId? fixedResourceId,
        string logicalDescriptor
    )
        where TFragment : class, IConfiglueFragment<TFragment> =>
        id is { } sourceId
            ? new StateSource<TFragment>(
                sourceId,
                serialized,
                new StateSourceOptions<TFragment>
                {
                    Priority = priority,
                    FallbackCondition = fallbackCondition,
                    PhysicalOrigin = physicalOrigin,
                    FixedResourceId = fixedResourceId,
                }
            )
            : new StateSource<TFragment>(
                serialized,
                new StateSourceOptions<TFragment>
                {
                    Priority = priority,
                    FallbackCondition = fallbackCondition,
                    PhysicalOrigin = physicalOrigin,
                    FixedResourceId = fixedResourceId,
                    LogicalDescriptor = logicalDescriptor,
                }
            );
}
