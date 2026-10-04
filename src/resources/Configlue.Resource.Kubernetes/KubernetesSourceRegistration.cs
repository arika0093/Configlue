using Configlue.Codecs;
using Configlue.Sources;

namespace Configlue.Resource.Kubernetes;

/// <summary>Options for registering a source backed by a Kubernetes ConfigMap or Secret.</summary>
/// <remarks>
/// Payloads compose through the canonical Resource + Codec path: single keys expose raw entry
/// bytes while whole-object mode (null <see cref="Key"/>) exposes deterministic sorted JSON.
/// No Codec logic is duplicated in this package.
/// </remarks>
public sealed class KubernetesSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>Whether the value comes from a ConfigMap or a Secret.</summary>
    public required KubernetesResourceKind Kind { get; init; }

    /// <summary>The object namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary>The object name.</summary>
    public required string Name { get; init; }

    /// <summary>The entry key, or null for deterministic whole-object mapping.</summary>
    public string? Key { get; init; }

    /// <summary>A directly supplied shared client. It remains caller-owned.</summary>
    public IKubernetesObjectClient? Client { get; init; }

    /// <summary>Resolves a shared client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, IKubernetesObjectClient>? ClientFactory { get; init; }

    /// <summary>The codec for the serialized payload.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Resource identity and watch settings.</summary>
    public KubernetesResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer. Writes are opt-in and resourceVersion-aware.</summary>
    public bool Writable { get; init; }

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }
}

/// <summary>Registers facade sources backed by Kubernetes ConfigMaps and Secrets.</summary>
public static class KubernetesSourceRegistration
{
    /// <summary>
    /// Adds a Kubernetes source. The supplied client remains externally owned.
    /// Watches use the native Kubernetes watch API; events invalidate and sources converge by re-reading.
    /// </summary>
    public static ConfiglueSourceRegistration FromKubernetes(
        this ConfiglueSourceSetBuilder sources,
        KubernetesSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        KubernetesResource.ValidateNamespace(options.Namespace);
        KubernetesResource.ValidateName(options.Name);
        if (options.Key is not null)
        {
            KubernetesResource.ValidateKey(options.Key);
        }

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
            new KubernetesSourceDefinition(options)
        );
    }

    /// <summary>Adds a source for one named ConfigMap key as a serialized resource.</summary>
    public static ConfiglueSourceRegistration FromKubernetesConfigMap(
        this ConfiglueSourceSetBuilder sources,
        string @namespace,
        string name,
        string? key,
        StateCodecBinding codec,
        IKubernetesObjectClient client,
        Action<KubernetesSourceOptionsBuilder>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        var builder = new KubernetesSourceOptionsBuilder
        {
            Kind = KubernetesResourceKind.ConfigMap,
            Namespace = @namespace,
            Name = name,
            Key = key,
            Codec = codec,
            Client = client,
        };
        configure?.Invoke(builder);
        return sources.FromKubernetes(builder.Build());
    }

    /// <summary>Adds a source for one named Secret key as a binary serialized resource.</summary>
    /// <remarks>Secret data receives the same redaction guarantees as cloud secret managers.</remarks>
    public static ConfiglueSourceRegistration FromKubernetesSecret(
        this ConfiglueSourceSetBuilder sources,
        string @namespace,
        string name,
        string? key,
        StateCodecBinding codec,
        IKubernetesObjectClient client,
        Action<KubernetesSourceOptionsBuilder>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        var builder = new KubernetesSourceOptionsBuilder
        {
            Kind = KubernetesResourceKind.Secret,
            Namespace = @namespace,
            Name = name,
            Key = key,
            Codec = codec,
            Client = client,
        };
        configure?.Invoke(builder);
        return sources.FromKubernetes(builder.Build());
    }

    /// <summary>Mutable builder for Kubernetes source options.</summary>
    public sealed class KubernetesSourceOptionsBuilder
    {
        /// <summary>The object kind.</summary>
        public KubernetesResourceKind Kind { get; set; }

        /// <summary>The object namespace.</summary>
        public string Namespace { get; set; } = string.Empty;

        /// <summary>The object name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The entry key, or null for whole-object mapping.</summary>
        public string? Key { get; set; }

        /// <summary>The codec for the serialized payload.</summary>
        public StateCodecBinding Codec { get; set; } = null!;

        /// <summary>A directly supplied shared client.</summary>
        public IKubernetesObjectClient? Client { get; set; }

        /// <summary>Resolves a shared client at context creation.</summary>
        public Func<IServiceProvider?, IKubernetesObjectClient>? ClientFactory { get; set; }

        /// <summary>An optional stable logical source ID.</summary>
        public string? Id { get; set; }

        /// <summary>Resource identity and watch settings.</summary>
        public KubernetesResourceOptions? ResourceOptions { get; set; }

        /// <summary>Higher values are read first.</summary>
        public int Priority { get; set; }

        /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
        public StateFallbackCondition FallbackCondition { get; set; } =
            StateFallbackCondition.NotFound;

        /// <summary>Whether this source exposes a writer.</summary>
        public bool Writable { get; set; }

        /// <summary>Additional context passed to the codec.</summary>
        public StateCodecContext CodecContext { get; set; }

        /// <summary>Builds immutable source options.</summary>
        public KubernetesSourceOptions Build() =>
            new()
            {
                Kind = Kind,
                Namespace = Namespace,
                Name = Name,
                Key = Key,
                Codec = Codec,
                Client = Client,
                ClientFactory = ClientFactory,
                Id = Id,
                ResourceOptions = ResourceOptions,
                Priority = Priority,
                FallbackCondition = FallbackCondition,
                Writable = Writable,
                CodecContext = CodecContext,
            };
    }

    private sealed class KubernetesSourceDefinition(KubernetesSourceOptions options)
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
                throw new InvalidOperationException("The Kubernetes client factory returned null.");
            }

            var resource = new KubernetesResource(
                client,
                options.Kind,
                options.Namespace,
                options.Name,
                options.Key,
                options.ResourceOptions
            );
            context.Own(resource);
            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                writer: options.Writable ? resource : null,
                watcher: resource
            );
            var scope = options.Key ?? "<object>";
            var physicalOrigin =
                options.Kind == KubernetesResourceKind.ConfigMap
                    ? $"k8s:configmap:{options.Namespace}/{options.Name}/{scope}"
                    : $"k8s:secret:{options.Namespace}/{options.Name}/{scope}";
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
                            options.Kind.ToString(),
                            options.Namespace,
                            options.Name,
                            options.Key ?? string.Empty
                        ),
                    }
                );
            return context.Complete(source);
        }
    }
}
