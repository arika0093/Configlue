using System.Text.Json;
using SharpYaml;

namespace Configlue.Provider.Yaml;

/// <summary>Options for registering a YAML file source through the one-arity facade.</summary>
public sealed class YamlFileSourceOptions
{
    /// <summary>An optional stable logical source ID used for provenance and explicit routing.</summary>
    public string? Id { get; init; }

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

    /// <summary>Whether this source is excluded from inferred ordinary write routing.</summary>
    public bool ExplicitOnly { get; init; }

    /// <summary>Whether to watch the file for changes.</summary>
    public bool WatchChanges { get; init; } = true;

    /// <summary>An optional property naming policy, such as <see cref="JsonNamingPolicy.CamelCase"/>.</summary>
    public JsonNamingPolicy? PropertyNamingPolicy { get; init; }

    /// <summary>Optional SharpYaml serializer metadata and behavior.</summary>
    public YamlSerializerOptions? SerializerOptions { get; init; }

    /// <summary>The persisted document structure. Reads accept both layouts; writes use the selected one.</summary>
    public DocumentLayoutOptions? DocumentLayout { get; init; }

    /// <summary>Optional absolute or relative directory URI used for the YAML language-server schema directive.</summary>
    public string? SchemaReferenceBaseUri { get; init; }

    /// <summary>Backup and retry settings for the helper-created file resource.</summary>
    public FileResourceOptions? ResourceOptions { get; init; }

    /// <summary>Byte transformers applied when reading and writing this source.</summary>
    public IReadOnlyList<IStateByteTransformer>? Transformers { get; init; }

    /// <summary>An optional stable physical identity; by default the normalized file path is used.</summary>
    public ResourceId? ResourceId { get; init; }
}

/// <summary>Registers facade sources backed by YAML files.</summary>
public static class YamlFileSourceRegistration
{
    /// <summary>Adds a YAML file source. The facade owns the created resource and its watcher.</summary>
    public static ConfiglueSourceRegistration FromYamlFile(
        this ConfiglueSourceSetBuilder sources,
        YamlFileSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
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

        return sources.Add(new YamlFileSourceDefinition(options));
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
            if (options.SchemaReferenceBaseUri is { } schemaReferenceBaseUri)
            {
                _ = StateSchemaReference.CreateUri(
                    schemaReferenceBaseUri,
                    modelSchema.ToMetadata()
                );
            }

            var file = new FileResource(
                options.Path,
                modelSchema.ToMetadata(),
                options.ResourceOptions,
                options.ResourceId
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
            var schemaShape = YamlDocumentEditor.CreateSchemaShape(
                modelSchema,
                options.PropertyNamingPolicy,
                options.SerializerOptions,
                options.DocumentLayout
            );
            IStateWatcher? resourceWatcher = options.WatchChanges ? file : null;
            var section = options.SectionPath is null
                ? YamlSectionResource.CreateRoot(
                    resource,
                    writer,
                    resourceWatcher,
                    options.ResourceId,
                    null,
                    schemaShape
                )
                : new YamlSectionResource(
                    resource,
                    writer,
                    options.SectionPath,
                    resourceWatcher,
                    options.ResourceId,
                    null,
                    schemaShape
                );
            resource = section;
            IResourceWriter? sourceWriter = writer is null ? null : section;
            var codec = new YamlStateCodec(
                options.PropertyNamingPolicy,
                modelSchema,
                options.SerializerOptions,
                options.DocumentLayout
            );
            var stateReader = new SerializedStateReader<TFragment>(resource, codec);
            var stateWriter = sourceWriter is null
                ? null
                : new SerializedStateWriter<TFragment>(
                    sourceWriter,
                    codec,
                    new StateCodecContext(null, null, options.SchemaReferenceBaseUri)
                );
            var physicalResourceId = options.ResourceId ?? (file as IResourceIdentity)?.ResourceId;
            return options.Id is { } id
                ? new StateSource<TFragment>(
                    id,
                    stateReader,
                    options.Priority,
                    options.FallbackCondition,
                    stateWriter,
                    resourceWatcher,
                    file.Path,
                    physicalResourceId,
                    explicitOnly: options.ExplicitOnly
                )
                : new StateSource<TFragment>(
                    YamlFileSourceSelector.CreateSourceId(file.Path, options.SectionPath),
                    stateReader,
                    options.Priority,
                    options.FallbackCondition,
                    stateWriter,
                    resourceWatcher,
                    file.Path,
                    physicalResourceId,
                    explicitOnly: options.ExplicitOnly
                );
        }
    }
}
