using Configlue.Codecs;
using Configlue.Sources;
using Google.Cloud.Storage.V1;

namespace Configlue.Resource.Gcs;

/// <summary>Options for registering a source backed by one Google Cloud Storage object.</summary>
public sealed class GcsObjectSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The GCS bucket name.</summary>
    public required string BucketName { get; init; }

    /// <summary>The object name within the bucket.</summary>
    public required string ObjectName { get; init; }

    /// <summary>A directly supplied client. It remains caller-owned.</summary>
    public StorageClient? Client { get; init; }

    /// <summary>Resolves a client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, StorageClient>? ClientFactory { get; init; }

    /// <summary>The codec for the serialized object.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Resource identity settings.</summary>
    public GcsObjectResourceOptions? ResourceOptions { get; init; }

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

/// <summary>Registers facade sources backed by Google Cloud Storage objects.</summary>
public static class GcsObjectSourceRegistration
{
    /// <summary>
    /// Adds a GCS object source. The supplied GCS client remains externally owned. When neither
    /// <see cref="GcsObjectSourceOptions.Client"/> nor
    /// <see cref="GcsObjectSourceOptions.ClientFactory"/> is configured, the source authenticates
    /// with Application Default Credentials.
    /// </summary>
    public static ConfiglueSourceRegistration FromGcsObject(
        this ConfiglueSourceSetBuilder sources,
        GcsObjectSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ObjectName);
        ArgumentNullException.ThrowIfNull(options.Codec);
        if (options is { Client: not null, ClientFactory: not null })
        {
            throw new ArgumentException(
                "Configure at most one of Client or ClientFactory.",
                nameof(options)
            );
        }

        if (options.Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new GcsObjectSourceDefinition(options)
        );
    }

    private sealed class GcsObjectSourceDefinition(GcsObjectSourceOptions options)
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
            var client =
                options.Client
                ?? options.ClientFactory?.Invoke(serviceProvider)
                ?? StorageClient.Create();
            if (client is null)
            {
                throw new InvalidOperationException("The GCS client factory returned null.");
            }

            var resource = new GcsObjectResource(
                client,
                options.BucketName,
                options.ObjectName,
                options.ResourceOptions
            );
            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                writer: options.Writable ? resource : null,
                watcher: resource
            );
            // Diagnostics carry bucket and object names only, never credentials or signed URLs.
            var physicalOrigin = $"gcs:{options.BucketName}";
            return ConfiglueSourceCompletion.WithDescribedIdentity(
                serialized,
                options.Id,
                string.Join(
                    "\n",
                    options.BucketName,
                    options.ObjectName,
                    options.ResourceOptions?.BucketNameSelector?.Method.ToString(),
                    options.ResourceOptions?.ObjectNameSelector?.Method.ToString(),
                    options.ResourceOptions?.ClientSelector?.Method.ToString()
                ),
                options.Priority,
                options.FallbackCondition,
                physicalOrigin,
                options.ResourceOptions?.FixedResourceId
            );
        }
    }
}
