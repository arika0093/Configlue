using Amazon.S3;
using Configlue.Codecs;
using Configlue.Sources;

namespace Configlue.Resource.S3;

/// <summary>Options for registering a source backed by one Amazon S3 object.</summary>
public sealed class S3ObjectSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The S3 bucket name.</summary>
    public required string BucketName { get; init; }

    /// <summary>The object key.</summary>
    public required string Key { get; init; }

    /// <summary>A directly supplied client. It remains caller-owned.</summary>
    public IAmazonS3? Client { get; init; }

    /// <summary>Resolves a client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, IAmazonS3>? ClientFactory { get; init; }

    /// <summary>The codec for the serialized object.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Resource identity settings.</summary>
    public S3ObjectResourceOptions? ResourceOptions { get; init; }

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

/// <summary>Registers facade sources backed by Amazon S3 objects.</summary>
public static class S3ObjectSourceRegistration
{
    /// <summary>Adds an S3 object source. The supplied S3 client remains externally owned.</summary>
    public static ConfiglueSourceRegistration FromS3Object(
        this ConfiglueSourceSetBuilder sources,
        S3ObjectSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BucketName);
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
            new S3ObjectSourceDefinition(options)
        );
    }

    private sealed class S3ObjectSourceDefinition(S3ObjectSourceOptions options)
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
                throw new InvalidOperationException("The S3 client factory returned null.");
            }

            var resource = new S3ObjectResource(
                client,
                options.BucketName,
                options.Key,
                options.ResourceOptions
            );
            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                writer: options.Writable ? resource : null
            );
            var physicalOrigin = $"s3:{options.BucketName}";
            return ConfiglueSourceCompletion.WithDescribedIdentity(
                serialized,
                options.Id,
                string.Join(
                    "\n",
                    options.BucketName,
                    options.Key,
                    options.ResourceOptions?.BucketNameSelector?.Method.ToString(),
                    options.ResourceOptions?.KeySelector?.Method.ToString(),
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
