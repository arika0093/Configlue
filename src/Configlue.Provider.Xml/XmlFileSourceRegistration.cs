namespace Configlue.Provider.Xml;

/// <summary>Options for registering an XML file source through the one-arity facade.</summary>
public sealed class XmlFileSourceOptions
{
    /// <summary>The stable logical source ID used for provenance and write routing.</summary>
    public required string Id { get; init; }

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

    /// <summary>Whether to watch the file for changes.</summary>
    public bool WatchChanges { get; init; } = true;

    /// <summary>Backup and retry settings for the helper-created file resource.</summary>
    public FileResourceOptions? ResourceOptions { get; init; }

    /// <summary>An optional stable physical identity; by default the normalized file path is used.</summary>
    public ResourceId? ResourceId { get; init; }
}

/// <summary>Registers facade sources backed by XML files.</summary>
public static class XmlFileSourceRegistration
{
    /// <summary>Adds an XML file source. The facade owns the created resource and its watcher.</summary>
    public static void FromXmlFile(
        this ConfiglueSourceSetBuilder sources,
        XmlFileSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        if (options.SectionPath is not null && string.IsNullOrWhiteSpace(options.SectionPath))
        {
            throw new ArgumentException("A section path cannot be empty.", nameof(options));
        }

        sources.Add(new XmlFileSourceDefinition(options));
    }

    private sealed class XmlFileSourceDefinition(XmlFileSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema modelSchema,
            IServiceProvider? serviceProvider,
            Action<IDisposable> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            var file = new FileResource(options.Path, options.ResourceOptions, options.ResourceId);
            ownResource(file);

            var writer = options.ReadOnly ? null : (IResourceWriter)file;
            IResourceWriter? sourceWriter = writer;
            IResourceReader resource = file;
            if (options.SectionPath is { } sectionPath)
            {
                var section = new XmlSectionResource(
                    file,
                    writer,
                    sectionPath,
                    options.WatchChanges ? file : null,
                    options.ResourceId
                );
                resource = section;
                sourceWriter = writer is null ? null : section;
            }
            IStateWatcher? watcher = options.WatchChanges ? file : null;
            var codec = new XmlStateCodec();
            var stateReader = new SerializedStateReader<TFragment>(resource, codec);
            var stateWriter = sourceWriter is null
                ? null
                : new SerializedStateWriter<TFragment>(sourceWriter, codec);
            return new StateSource<TFragment>(
                options.Id,
                stateReader,
                options.Priority,
                options.FallbackCondition,
                stateWriter,
                watcher,
                file.Path,
                options.ResourceId ?? (file as IResourceIdentity)?.ResourceId
            );
        }
    }
}
