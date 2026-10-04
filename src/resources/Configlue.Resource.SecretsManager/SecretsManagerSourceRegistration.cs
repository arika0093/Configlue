using Amazon.SecretsManager;
using Configlue.Codecs;
using Configlue.Sources;

namespace Configlue.Resource.SecretsManager;

/// <summary>Options for registering a source backed by one AWS Secrets Manager secret.</summary>
/// <remarks>
/// Least-privilege IAM for read-only mode (<see cref="Writable"/> is <see langword="false"/>
/// by default): <c>secretsmanager:GetSecretValue</c> and <c>secretsmanager:DescribeSecret</c> on
/// the secret, plus <c>kms:Decrypt</c> on its KMS key when the secret uses a customer managed
/// key. Writable mode additionally requires <c>secretsmanager:PutSecretValue</c> on the secret
/// and, when staging-label promotion is used, <c>secretsmanager:UpdateSecretVersionStage</c>.
/// </remarks>
public sealed class SecretsManagerSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The secret ARN or name.</summary>
    public required string SecretId { get; init; }

    /// <summary>A directly supplied client. It remains caller-owned.</summary>
    public IAmazonSecretsManager? Client { get; init; }

    /// <summary>Resolves a client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, IAmazonSecretsManager>? ClientFactory { get; init; }

    /// <summary>The codec for the serialized secret payload.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Version selection, watching, write, and identity settings.</summary>
    public SecretsManagerResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>
    /// Whether this source exposes a writer. Writes are opt-in and add a new secret version
    /// with unchecked semantics; Secrets Manager offers no atomic conditional-write precondition.
    /// </summary>
    public bool Writable { get; init; }

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }
}

/// <summary>Registers facade sources backed by AWS Secrets Manager secrets.</summary>
public static class SecretsManagerSourceRegistration
{
    /// <summary>Adds a Secrets Manager secret source. The supplied client remains externally owned.</summary>
    public static ConfiglueSourceRegistration FromSecretsManager(
        this ConfiglueSourceSetBuilder sources,
        SecretsManagerSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SecretId);
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
            new SecretsManagerSourceDefinition(options)
        );
    }

    private sealed class SecretsManagerSourceDefinition(SecretsManagerSourceOptions options)
        : IConfiglueSourceDefinition
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
                    "The Secrets Manager client factory returned null."
                );
            }

            var resource = new SecretsManagerResource(
                client,
                options.SecretId,
                options.ResourceOptions
            );
            context.Own(resource);
            return context.Complete(CreateSourceCore<TFragment>(context.ModelSchema, resource));
        }

        private StateSource<TFragment> CreateSourceCore<TFragment>(
            ConfiglueModelSchema modelSchema,
            SecretsManagerResource resource
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(modelSchema);
            ArgumentNullException.ThrowIfNull(resource);
            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                writer: options.Writable ? resource : null,
                watcher: resource.IsWatchSupported ? resource : null
            );
            var physicalOrigin = $"secretsmanager:{options.SecretId}";
            return options.Id is { } id
                ? new StateSource<TFragment>(
                    id,
                    serialized,
                    new StateSourceOptions<TFragment>
                    {
                        Priority = options.Priority,
                        FallbackCondition = options.FallbackCondition,
                        PhysicalOrigin = physicalOrigin,
                        FixedResourceId = options.ResourceOptions?.FixedResourceId,
                    }
                )
                : new StateSource<TFragment>(
                    serialized,
                    new StateSourceOptions<TFragment>
                    {
                        Priority = options.Priority,
                        FallbackCondition = options.FallbackCondition,
                        PhysicalOrigin = physicalOrigin,
                        FixedResourceId = options.ResourceOptions?.FixedResourceId,
                        LogicalDescriptor = string.Join(
                            "\n",
                            options.SecretId,
                            options.ResourceOptions?.VersionId,
                            options.ResourceOptions?.VersionStage,
                            options.ResourceOptions?.SecretIdSelector?.Method.ToString(),
                            options.ResourceOptions?.VersionIdSelector?.Method.ToString(),
                            options.ResourceOptions?.VersionStageSelector?.Method.ToString(),
                            options.ResourceOptions?.ClientSelector?.Method.ToString()
                        ),
                    }
                );
        }
    }
}
