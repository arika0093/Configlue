using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Configlue.Provider.Yaml;

/// <summary>Options for registering a YAML file source through the one-arity facade.</summary>
public sealed class YamlFileSourceOptions
{
    /// <summary>The stable logical source ID used for provenance and write routing.</summary>
    public required string Id { get; init; }

    /// <summary>The YAML file path.</summary>
    public required string Path { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>An optional colon- or double-underscore-separated YAML section path.</summary>
    public string? SectionPath { get; init; }

    /// <summary>Whether the source is read-only.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Whether to watch the file for changes.</summary>
    public bool WatchChanges { get; init; } = true;

    /// <summary>An optional YamlDotNet property naming convention.</summary>
    public INamingConvention? NamingConvention { get; init; }

    /// <summary>Backup and retry settings for the helper-created file resource.</summary>
    public FileResourceOptions? ResourceOptions { get; init; }

    /// <summary>An optional stable physical identity; by default the normalized file path is used.</summary>
    public ResourceId? ResourceId { get; init; }
}

/// <summary>Registers facade sources backed by YAML files.</summary>
public static class YamlFileSourceRegistration
{
    /// <summary>Adds a YAML file source. The facade owns the created resource and its watcher.</summary>
    public static void FromYamlFile(
        this ConfiglueSourceSetBuilder sources,
        YamlFileSourceOptions options
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

        sources.Add(new YamlFileSourceDefinition(options));
    }

    private sealed class YamlFileSourceDefinition(YamlFileSourceOptions options)
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
            IResourceReader resource = file;
            if (options.SectionPath is { } sectionPath)
            {
                resource = new YamlSectionResource(
                    file,
                    writer,
                    sectionPath,
                    options.WatchChanges ? file : null,
                    options.ResourceId
                );
            }
            IStateWatcher? watcher = options.WatchChanges ? file : null;
            var codec = new YamlStateCodec(options.NamingConvention);
            var stateReader = new SerializedStateReader<TFragment>(resource, codec);
            var stateWriter = writer is null
                ? null
                : new SerializedStateWriter<TFragment>(writer, codec);
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
