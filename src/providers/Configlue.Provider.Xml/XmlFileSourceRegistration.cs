using Configlue.Codecs;
using Configlue.Sources;

namespace Configlue.Provider.Xml;

/// <summary>Options for registering an XML file source through the one-arity facade.</summary>
public sealed class XmlFileSourceOptions
{
    /// <summary>An optional stable logical source ID used for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The XML file path.</summary>
    public required string Path { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>An optional colon- or double-underscore-separated XML element path.</summary>
    public string? SectionPath { get; init; }

    /// <summary>Whether the source is read-only.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Whether this source is excluded from inferred ordinary write routing.</summary>
    public bool ExplicitOnly { get; init; }

    /// <summary>Whether to watch the file for changes.</summary>
    public bool WatchChanges { get; init; } = true;

    /// <summary>Backup and retry settings for the helper-created file resource.</summary>
    public FileResourceOptions? ResourceOptions { get; init; }

    /// <summary>Byte transformers applied when reading and writing this source.</summary>
    public IReadOnlyList<IStateByteTransformer>? Transformers { get; init; }

    /// <summary>
    /// An advanced fixed identity override shared by every operation context; by default the
    /// normalized file path is used. A configured value asserts one physical coordination domain.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }
}

/// <summary>Registers facade sources backed by XML files.</summary>
public static class XmlFileSourceRegistration
{
    /// <summary>Adds an XML file source. The facade owns the created resource and its watcher.</summary>
    public static ConfiglueSourceRegistration FromXmlFile(
        this ConfiglueSourceSetBuilder sources,
        XmlFileSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        if (options.SectionPath is not null && string.IsNullOrWhiteSpace(options.SectionPath))
        {
            throw new ArgumentException("A section path cannot be empty.", nameof(options));
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new XmlFileSourceDefinition(options)
        );
    }

    private sealed class XmlFileSourceDefinition(XmlFileSourceOptions options)
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
                options.FixedResourceId,
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
            IResourceWriter? sourceWriter = writer;
            if (options.SectionPath is { } sectionPath)
            {
                var section = new XmlSectionResource(
                    resource,
                    writer,
                    sectionPath,
                    options.WatchChanges ? file : null,
                    options.FixedResourceId
                );
                resource = section;
                sourceWriter = writer is null ? null : section;
            }
            ISourceWatcher? watcher = options.WatchChanges ? file : null;
            var codec = StateCodecBinding.Dynamic(new XmlStateCodec());
            var serialized = new SerializedSource<TFragment>(
                resource,
                codec,
                writer: sourceWriter,
                watcher: watcher
            );
            var fixedResourceId = options.FixedResourceId;
            return options.Id is { } id
                ? new StateSource<TFragment>(
                    id,
                    serialized,
                    new StateSourceOptions<TFragment>
                    {
                        Priority = options.Priority,
                        FallbackCondition = options.FallbackCondition,
                        PhysicalOrigin = file.Path,
                        FixedResourceId = fixedResourceId,
                        ExplicitOnly = options.ExplicitOnly,
                    }
                )
                : new StateSource<TFragment>(
                    XmlFileSourceSelector.CreateSourceId(file.Path, options.SectionPath),
                    serialized,
                    new StateSourceOptions<TFragment>
                    {
                        Priority = options.Priority,
                        FallbackCondition = options.FallbackCondition,
                        PhysicalOrigin = file.Path,
                        FixedResourceId = fixedResourceId,
                        ExplicitOnly = options.ExplicitOnly,
                    }
                );
        }
    }
}
