using System.Security.Cryptography;
using System.Text;
using Configlue.CompilerServices;
using Configlue.Sources;
using MessagePack;

namespace Configlue.Provider.MessagePack;

/// <summary>Options for registering a MessagePack file source through the one-arity facade.</summary>
public sealed class MessagePackFileSourceOptions
{
    /// <summary>An optional stable logical source ID used for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The MessagePack file path.</summary>
    public required string Path { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether the source is read-only.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Whether this source is excluded from inferred ordinary write routing.</summary>
    public bool ExplicitOnly { get; init; }

    /// <summary>Whether to watch the file for changes.</summary>
    public bool WatchChanges { get; init; } = true;

    /// <summary>MessagePack resolver, security, and compression options.</summary>
    public MessagePackSerializerOptions? SerializerOptions { get; init; }

    /// <summary>Backup and retry settings for the helper-created file resource.</summary>
    public FileResourceOptions? ResourceOptions { get; init; }

    /// <summary>Byte transformers applied when reading and writing this source.</summary>
    public IReadOnlyList<IStateByteTransformer>? Transformers { get; init; }

    /// <summary>An optional stable physical identity; by default the normalized file path is used.</summary>
    public ResourceId? ResourceId { get; init; }

    /// <summary>An optional document section. MessagePack sources do not support sections.</summary>
    public string? SectionPath { get; init; }
}

/// <summary>Registers facade sources backed by MessagePack documents.</summary>
public static class MessagePackFileSourceRegistration
{
    /// <summary>Adds a MessagePack file source. The facade owns the created resource and its watcher.</summary>
    public static ConfiglueSourceRegistration FromMessagePackFile(
        this ConfiglueSourceSetBuilder sources,
        MessagePackFileSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        if (options.SectionPath is not null)
        {
            throw new NotSupportedException(
                "MessagePack file sources do not support document sections."
            );
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new MessagePackFileSourceDefinition(options)
        );
    }

    /// <summary>Registers one MessagePack file source as the normal write destination for this model.</summary>
    public static void UseMessagePackFile<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        MessagePackFileSourceOptions options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        model.Sources(sources => sources.FromMessagePackFile(options));
        if (!options.ReadOnly && options.Id is { } id)
        {
            model.Writes(write => write.DefaultTo(id));
        }
    }

    /// <summary>Registers one MessagePack file source at the supplied path as the normal write destination.</summary>
    public static void UseMessagePackFile<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        string path
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        model.UseMessagePackFile(new MessagePackFileSourceOptions { Path = path });
    }

    internal static string CreateSourceId(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            canonicalPath = canonicalPath.ToUpperInvariant();
        }

        canonicalPath = canonicalPath.Normalize(NormalizationForm.FormKC);
        var identity = $"configlue-messagepack-file-v1\n{canonicalPath}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "messagepack-file:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class MessagePackFileSourceDefinition(MessagePackFileSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            return context.Complete(
                CreateSourceCore<TFragment>(context.ModelSchema, context.HostPaths, context.Own)
            );
        }

        private StateSource<TFragment> CreateSourceCore<TFragment>(
            ConfiglueModelSchema modelSchema,
            IConfiglueHostPaths hostPaths,
            Action<object> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            var file = new FileResource(
                options.Path,
                modelSchema.ToMetadata(),
                options.ResourceOptions,
                options.ResourceId,
                hostPaths
            );
            ownResource(file);

            IResourceReader resource = file;
            IResourceWriter? writer = options.ReadOnly ? null : file;
            if (options.Transformers is { Count: > 0 })
            {
                var transformed = new TransformingResource(file, options.Transformers);
                resource = transformed;
                writer = options.ReadOnly ? null : transformed.Writer;
            }

            var codec = new MessagePackStateCodec<TFragment>(options.SerializerOptions);
            var serialized = new SerializedSource<TFragment>(
                resource,
                codec,
                writer: writer,
                watcher: options.WatchChanges ? file : null
            );
            var physicalResourceId = options.ResourceId ?? (file as IResourceIdentity)?.ResourceId;
            return options.Id is { } id
                ? new StateSource<TFragment>(
                    id,
                    serialized,
                    options.Priority,
                    options.FallbackCondition,
                    physicalOrigin: file.Path,
                    resourceId: physicalResourceId,
                    explicitOnly: options.ExplicitOnly
                )
                : new StateSource<TFragment>(
                    CreateSourceId(options.Path),
                    serialized,
                    options.Priority,
                    options.FallbackCondition,
                    physicalOrigin: file.Path,
                    resourceId: physicalResourceId,
                    explicitOnly: options.ExplicitOnly
                );
        }
    }
}
