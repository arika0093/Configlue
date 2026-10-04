using Configlue.Codecs;
using Configlue.Sources;

namespace Configlue.Resource.Vault;

/// <summary>Options for registering a source backed by one Vault KV secret.</summary>
public sealed class VaultKvSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The KV mount.</summary>
    public required string Mount { get; init; }

    /// <summary>The secret path within the mount.</summary>
    public required string Path { get; init; }

    /// <summary>A directly supplied client. It remains caller-owned.</summary>
    /// <remarks>Authentication (such as token injection) is configured on the client itself.</remarks>
    public IVaultKvClient? Client { get; init; }

    /// <summary>Resolves a client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, IVaultKvClient>? ClientFactory { get; init; }

    /// <summary>The codec for the serialized secret payload.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Resource identity, engine-version, and polling settings.</summary>
    public VaultKvResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer. Writes use KV v2 check-and-set when a baseline version is available.</summary>
    public bool Writable { get; init; } = true;

    /// <summary>Whether this source exposes a polling change watcher. Vault KV has no streaming watch.</summary>
    public bool EnableWatch { get; init; } = true;

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }
}

/// <summary>Registers facade sources backed by HashiCorp Vault KV secrets.</summary>
public static class VaultKvSourceRegistration
{
    /// <summary>Adds a Vault KV secret source. The supplied client remains externally owned.</summary>
    public static ConfiglueSourceRegistration FromVaultKv(
        this ConfiglueSourceSetBuilder sources,
        VaultKvSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        VaultKvPath.ValidateMount(options.Mount);
        VaultKvPath.ValidatePath(options.Path);
        ArgumentNullException.ThrowIfNull(options.Codec);
        if (options.ResourceOptions is { } resourceOptions)
        {
            VaultKvResourceOptions.Validate(resourceOptions, nameof(options));
        }
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
            new VaultKvSourceDefinition(options)
        );
    }

    private sealed class VaultKvSourceDefinition(VaultKvSourceOptions options)
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
                throw new InvalidOperationException("The Vault client factory returned null.");
            }

            var resource = new VaultKvResource(
                client,
                options.Mount,
                options.Path,
                options.ResourceOptions
            );
            context.Own(resource);

            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                writer: options.Writable ? resource : null,
                watcher: options.EnableWatch ? resource : null
            );
            var physicalOrigin = $"vault:{options.Mount}/{options.Path}";
            StateSource<TFragment> source = options.Id is { } id
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
                            options.Mount,
                            options.Path,
                            options.ResourceOptions?.KvVersion.ToString(),
                            options.ResourceOptions?.PollingInterval.ToString(),
                            options.ResourceOptions?.MountSelector?.Method.ToString(),
                            options.ResourceOptions?.PathSelector?.Method.ToString(),
                            options.ResourceOptions?.ClientSelector?.Method.ToString()
                        ),
                    }
                );
            return context.Complete(source);
        }
    }
}
