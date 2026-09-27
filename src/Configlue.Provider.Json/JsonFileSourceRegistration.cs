using System.Text.Json;

namespace Configlue.Provider.Json;

/// <summary>Options for registering a JSON file source through the one-arity facade.</summary>
public sealed class JsonFileSourceOptions
{
    /// <summary>The stable logical source ID used for provenance and write routing.</summary>
    public required string Id { get; init; }

    /// <summary>The JSON file path.</summary>
    public required string Path { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>An optional colon- or double-underscore-separated JSON section path.</summary>
    public string? SectionPath { get; init; }

    /// <summary>Whether the source is read-only.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Whether to watch the file for changes.</summary>
    public bool WatchChanges { get; init; } = true;

    /// <summary>JSON serialization and property naming options.</summary>
    public JsonSerializerOptions? SerializerOptions { get; init; }

    /// <summary>Optional absolute or relative directory URI used for the versioned instance <c>$schema</c> reference.</summary>
    public string? SchemaReferenceBaseUri { get; init; }

    /// <summary>Backup and retry settings for the helper-created file resource.</summary>
    public FileResourceOptions? ResourceOptions { get; init; }

    /// <summary>An optional stable physical identity; by default the normalized file path is used.</summary>
    public ResourceId? ResourceId { get; init; }
}

/// <summary>Registers facade sources backed by JSON files.</summary>
public static class JsonFileSourceRegistration
{
    /// <summary>Adds a JSON file source. The facade owns the created resource and its watcher.</summary>
    public static void FromJsonFile(
        this ConfiglueSourceSetBuilder sources,
        JsonFileSourceOptions options
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
        if (options.SectionPath is not null && options.SchemaReferenceBaseUri is not null)
        {
            throw new ArgumentException(
                "A root schema reference cannot be added to a section source.",
                nameof(options)
            );
        }

        sources.Add(new JsonFileSourceDefinition(options));
    }

    private sealed class JsonFileSourceDefinition(JsonFileSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema modelSchema,
            IServiceProvider? serviceProvider,
            Action<IDisposable> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            if (options.SchemaReferenceBaseUri is { } schemaReferenceBaseUri)
            {
                _ = StateSchemaReference.CreateUri(
                    schemaReferenceBaseUri,
                    modelSchema.ToMetadata()
                );
            }

            var file = new FileResource(options.Path, options.ResourceOptions, options.ResourceId);
            ownResource(file);

            var writer = options.ReadOnly ? null : (IResourceWriter)file;
            IResourceWriter? sourceWriter = writer;
            IResourceReader resource = file;
            if (options.SectionPath is { } sectionPath)
            {
                var section = new JsonSectionResource(
                    file,
                    writer,
                    sectionPath,
                    options.WatchChanges ? file : null,
                    options.SerializerOptions,
                    options.ResourceId
                );
                resource = section;
                sourceWriter = writer is null ? null : section;
            }
            IStateWatcher? watcher = options.WatchChanges ? file : null;
            var codec = new JsonStateCodec(options.SerializerOptions);
            var stateReader = new SerializedStateReader<TFragment>(resource, codec);
            var stateWriter = sourceWriter is null
                ? null
                : new SerializedStateWriter<TFragment>(
                    sourceWriter,
                    codec,
                    new StateCodecContext(null, null, options.SchemaReferenceBaseUri)
                );
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
