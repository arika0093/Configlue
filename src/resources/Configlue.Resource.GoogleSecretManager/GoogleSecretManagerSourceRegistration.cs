using Configlue.Codecs;
using Configlue.Sources;

namespace Configlue.Resource.GoogleSecretManager;

/// <summary>Options for registering a source backed by one Google Cloud Secret Manager secret version.</summary>
/// <remarks>
/// <para>
/// Reads require <c>secretmanager.versions.access</c> (payload) and, when alias watching is enabled,
/// <c>secretmanager.versions.get</c> (metadata-only polling). Adding versions requires
/// <c>secretmanager.versions.add</c> on the parent secret. Writes are opt-in via <see cref="Writable"/>
/// and always append a new immutable version; aliases are never moved and secret metadata is never
/// mutated by writes.
/// </para>
/// <para>
/// Supply credentials by injecting a configured <see cref="IGoogleSecretManagerClient"/> through
/// <see cref="Client"/> or <see cref="ClientFactory"/>. For Application Default Credentials, resolve
/// the Google SDK client with ADC in <see cref="ClientFactory"/> (for example
/// <c>SecretManagerServiceClient.Create()</c> adapted to <see cref="IGoogleSecretManagerClient"/>)
/// and return it; this package never captures credential material itself.
/// </para>
/// </remarks>
public sealed class GoogleSecretManagerSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The Google Cloud project ID.</summary>
    public required string ProjectId { get; init; }

    /// <summary>The secret ID.</summary>
    public required string SecretId { get; init; }

    /// <summary>The Google Cloud location for regional secrets, or null for the default global secret.</summary>
    public string? Location { get; init; }

    /// <summary>The version selector: a fixed numeric version, <c>"latest"</c>, or an alias.</summary>
    public string Version { get; init; } = GoogleSecretManagerResource.LatestVersion;

    /// <summary>A directly supplied client. It remains caller-owned.</summary>
    public IGoogleSecretManagerClient? Client { get; init; }

    /// <summary>
    /// Resolves a client at context creation, for example from dependency injection or Application
    /// Default Credentials. The returned client remains owned by the factory or service that supplied it.
    /// </summary>
    public Func<IServiceProvider?, IGoogleSecretManagerClient>? ClientFactory { get; init; }

    /// <summary>The codec for the serialized secret payload.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Resource identity, polling, and selector settings.</summary>
    public GoogleSecretManagerResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>
    /// Whether this source exposes a writer that appends new secret versions. Defaults to read-only.
    /// </summary>
    public bool Writable { get; init; }

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }
}

/// <summary>Registers facade sources backed by Google Cloud Secret Manager secret versions.</summary>
public static class GoogleSecretManagerSourceRegistration
{
    /// <summary>
    /// Adds a Secret Manager source. The supplied client remains externally owned. Writes are opt-in
    /// via <see cref="GoogleSecretManagerSourceOptions.Writable"/> and append new versions; fixed
    /// versions and disabled watching never create watchers.
    /// </summary>
    public static ConfiglueSourceRegistration FromGoogleSecretManager(
        this ConfiglueSourceSetBuilder sources,
        GoogleSecretManagerSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SecretId);
        if (options.Location is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Location);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.Version);
        ArgumentNullException.ThrowIfNull(options.Codec);
        options.ResourceOptions?.Validate();
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
            new GoogleSecretManagerSourceDefinition(options)
        );
    }

    private sealed class GoogleSecretManagerSourceDefinition(
        GoogleSecretManagerSourceOptions options
    ) : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var client = options.Client ?? options.ClientFactory!(context.Services);
            if (client is null)
            {
                throw new InvalidOperationException(
                    "The Secret Manager client factory returned null."
                );
            }

            var resourceOptions = MergeResourceOptions(options);
            var resource = new GoogleSecretManagerResource(
                client,
                options.ProjectId,
                options.SecretId,
                resourceOptions
            );
            context.Own(resource);

            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                writer: options.Writable ? resource : null,
                watcher: ShouldWatch(resourceOptions) ? resource : null
            );
            var physicalOrigin = string.IsNullOrEmpty(resourceOptions.Location)
                ? $"googlesecrets:{options.ProjectId}/{options.SecretId}"
                : $"googlesecrets:{options.ProjectId}/{resourceOptions.Location}/{options.SecretId}";
            var source = ConfiglueSourceCompletion.WithDescribedIdentity(
                serialized,
                options.Id,
                string.Join(
                    "\n",
                    options.ProjectId,
                    resourceOptions.Location ?? string.Empty,
                    options.SecretId,
                    resourceOptions.Version,
                    resourceOptions.ProjectIdSelector?.Method.ToString(),
                    resourceOptions.SecretIdSelector?.Method.ToString(),
                    resourceOptions.LocationSelector?.Method.ToString(),
                    resourceOptions.VersionSelector?.Method.ToString(),
                    resourceOptions.ClientSelector?.Method.ToString()
                ),
                options.Priority,
                options.FallbackCondition,
                physicalOrigin,
                resourceOptions.FixedResourceId
            );
            return context.Complete(source);
        }
    }

    internal static GoogleSecretManagerResourceOptions MergeResourceOptions(
        GoogleSecretManagerSourceOptions options
    ) => MergeResourceOptionsCore(options);

    internal static bool ShouldWatch(GoogleSecretManagerResourceOptions resourceOptions) =>
        ShouldWatchCore(resourceOptions);

    private static GoogleSecretManagerResourceOptions MergeResourceOptionsCore(
        GoogleSecretManagerSourceOptions options
    )
    {
        var configured = options.ResourceOptions;
        return new GoogleSecretManagerResourceOptions
        {
            Location = configured?.Location ?? options.Location,
            Version = configured?.Version ?? options.Version,
            ProjectIdSelector = configured?.ProjectIdSelector,
            SecretIdSelector = configured?.SecretIdSelector,
            LocationSelector = configured?.LocationSelector,
            VersionSelector = configured?.VersionSelector,
            ClientSelector = configured?.ClientSelector,
            FixedResourceId = configured?.FixedResourceId,
            PollInterval = configured?.PollInterval ?? TimeSpan.FromSeconds(30),
            EnableWrites = options.Writable,
            EnableWatching = configured?.EnableWatching ?? false,
        };
    }

    private static bool ShouldWatchCore(GoogleSecretManagerResourceOptions resourceOptions)
    {
        if (!resourceOptions.EnableWatching)
        {
            return false;
        }

        if (resourceOptions.VersionSelector is not null)
        {
            return true;
        }

        return !GoogleSecretManagerResource.IsFixedNumericVersion(resourceOptions.Version);
    }
}
