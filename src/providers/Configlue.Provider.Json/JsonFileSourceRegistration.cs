using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Configlue.CompilerServices;
using Configlue.Sources;

namespace Configlue.Provider.Json;

/// <summary>Options for registering a JSON file source through the one-arity facade.</summary>
public sealed class JsonFileSourceOptions
{
    /// <summary>An optional stable logical source ID used for provenance and explicit routing.</summary>
    public string? Id { get; init; }

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

    /// <summary>Whether this source is excluded from inferred ordinary write routing.</summary>
    public bool ExplicitOnly { get; init; }

    /// <summary>Whether to watch the file for changes.</summary>
    public bool WatchChanges { get; init; } = true;

    /// <summary>JSON serialization and property naming options.</summary>
    public JsonSerializerOptions? SerializerOptions { get; init; }

    /// <summary>The persisted document structure. Reads accept both layouts; writes use the selected one.</summary>
    public DocumentLayoutOptions? DocumentLayout { get; init; }

    /// <summary>Optional absolute or relative directory URI used for the versioned instance <c>$schema</c> reference.</summary>
    public string? SchemaReferenceBaseUri { get; init; }

    /// <summary>Backup and retry settings for the helper-created file resource.</summary>
    public FileResourceOptions? ResourceOptions { get; init; }

    /// <summary>Byte transformers applied when reading and writing this source.</summary>
    public IReadOnlyList<IStateByteTransformer>? Transformers { get; init; }

    /// <summary>An optional stable physical identity; by default the normalized file path is used.</summary>
    public ResourceId? ResourceId { get; init; }

    internal string? MountPath { get; set; }

    internal string? SectionPathOverride { get; set; }

    internal int? PriorityOverride { get; set; }

    internal StateFallbackCondition? FallbackConditionOverride { get; set; }

    internal bool? ReadOnlyOverride { get; set; }

    internal bool? WatchChangesOverride { get; set; }

    internal JsonSerializerOptions? SerializerOptionsOverride { get; set; }

    internal bool? ExplicitOnlyOverride { get; set; }

    internal string? IdOverride { get; set; }
}

/// <summary>Registers facade sources backed by JSON files.</summary>
public static class JsonFileSourceRegistration
{
    /// <summary>Begins a fluent JSON file source registration.</summary>
    public static JsonFileRegistration<TModel> JsonFile<TModel>(
        this ConfiglueSourceSetBuilder<TModel> sources,
        string path
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var options = new JsonFileSourceOptions { Path = path };
        sources.FromJsonFile(options);
        return new JsonFileRegistration<TModel>(options, sources);
    }

    /// <summary>Registers the default JSON settings file beside the application executable.</summary>
    public static void UseDefaultJsonFile<TModel>(this ConfiglueModelBuilder<TModel> model)
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        model.UseJsonFile(
            new JsonFileSourceOptions
            {
                Path = Path.Combine(AppContext.BaseDirectory, "usersettings.json"),
            }
        );
    }

    /// <summary>Registers one JSON file source as the normal write destination for this model.</summary>
    public static void UseJsonFile<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        JsonFileSourceOptions options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        model.Sources(sources => sources.FromJsonFile(options));
        if (!options.ReadOnly && options.Id is { } id)
        {
            model.Writes(write => write.DefaultTo(id));
        }
    }

    /// <summary>Registers one JSON file source at the supplied path as the normal write destination.</summary>
    public static void UseJsonFile<TModel>(this ConfiglueModelBuilder<TModel> model, string path)
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        model.UseJsonFile(new JsonFileSourceOptions { Path = path });
    }

    /// <summary>Adds a JSON file source. The facade owns the created resource and its watcher.</summary>
    public static ConfiglueSourceRegistration FromJsonFile(
        this ConfiglueSourceSetBuilder sources,
        JsonFileSourceOptions options
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

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new JsonFileSourceDefinition(options)
        );
    }

    private sealed class JsonFileSourceDefinition(JsonFileSourceOptions options)
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
            Action<IDisposable> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            if (options.MountPath is null)
            {
                return CreateSource<TFragment>(modelSchema, hostPaths, ownResource);
            }

            var path = options.MountPath.Split(new[] { '.' }, StringSplitOptions.None);
            var subtreeSchema = GetNestedSchema(modelSchema, path, 0, options.MountPath);
            var fragmentType = subtreeSchema.CreateEmptyFragment().GetType();
            var method = typeof(JsonFileSourceDefinition)
                .GetMethod(
                    nameof(CreateMountedSource),
                    BindingFlags.Instance | BindingFlags.Public
                )!
                .MakeGenericMethod(typeof(TFragment), fragmentType);
            try
            {
                return (StateSource<TFragment>)
                    method.Invoke(
                        this,
                        [modelSchema, subtreeSchema, path, ownResource, hostPaths]
                    )!;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        public StateSource<TRootFragment> CreateMountedSource<TRootFragment, TSubtreeFragment>(
            ConfiglueModelSchema rootSchema,
            ConfiglueModelSchema subtreeSchema,
            string[] path,
            Action<IDisposable> ownResource,
            IConfiglueHostPaths hostPaths
        )
            where TRootFragment : class, IConfiglueFragment<TRootFragment>
            where TSubtreeFragment : class, IConfiglueFragment<TSubtreeFragment>
        {
            var source = CreateSource<TSubtreeFragment>(subtreeSchema, hostPaths, ownResource);
            return StateSourceProjection.Mount<TSubtreeFragment, TRootFragment>(
                source,
                string.Join(".", path),
                rootSchema.ToMetadata()
            );
        }

        private StateSource<TFragment> CreateSource<TFragment>(
            ConfiglueModelSchema modelSchema,
            IConfiglueHostPaths hostPaths,
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
                options.ResourceId,
                hostPaths
            );
            ownResource(file);

            var readOnly = options.ReadOnlyOverride ?? options.ReadOnly;
            var watchChanges = options.WatchChangesOverride ?? options.WatchChanges;
            var sectionPath = options.SectionPathOverride ?? options.SectionPath;
            var priority = options.PriorityOverride ?? options.Priority;
            var fallbackCondition = options.FallbackConditionOverride ?? options.FallbackCondition;
            var explicitOnly = options.ExplicitOnlyOverride ?? options.ExplicitOnly;
            var serializerOptions = options.SerializerOptionsOverride ?? options.SerializerOptions;
            var schemaShape = JsoncDocumentEditor.CreateSchemaShape<TFragment>(
                modelSchema,
                serializerOptions,
                options.DocumentLayout,
                options.SchemaReferenceBaseUri
            );
            IResourceReader resource = file;
            IResourceWriter? writer = readOnly ? null : file;
            ISourceWatcher? resourceWatcher = watchChanges ? file : null;
            if (options.Transformers is { Count: > 0 })
            {
                var transformed = new TransformingResource(file, options.Transformers);
                resource = transformed;
                writer = readOnly ? null : transformed.Writer;
            }
            var section = sectionPath is null
                ? JsonSectionResource.CreateRoot(
                    resource,
                    writer,
                    resourceWatcher,
                    serializerOptions,
                    options.ResourceId,
                    schemaShape
                )
                : new JsonSectionResource(
                    resource,
                    writer,
                    sectionPath,
                    resourceWatcher,
                    serializerOptions,
                    options.ResourceId,
                    schemaShape
                );
            resource = section;
            IResourceWriter? sourceWriter = writer is null ? null : section;
            var codec = new JsonStateCodec<TFragment>(
                serializerOptions,
                ConfiglueJsonFragmentRegistry<TFragment>.Converter,
                options.DocumentLayout
            );
            var serialized = new SerializedSource<TFragment>(
                resource,
                codec,
                new StateCodecContext(null, null, options.SchemaReferenceBaseUri),
                writer: sourceWriter,
                watcher: resourceWatcher
            );
            var physicalResourceId = options.ResourceId ?? (file as IResourceIdentity)?.ResourceId;
            var sourceId =
                options.IdOverride
                ?? options.Id
                ?? JsonFileSourceSelector.CreateSourceId(
                    options.Path,
                    sectionPath,
                    options.MountPath
                );
            return new StateSource<TFragment>(
                sourceId,
                serialized,
                priority,
                fallbackCondition,
                physicalOrigin: file.Path,
                resourceId: physicalResourceId,
                explicitOnly: explicitOnly
            );
        }

        private static ConfiglueModelSchema GetNestedSchema(
            ConfiglueModelSchema schema,
            IReadOnlyList<string> path,
            int index,
            string propertyPath
        )
        {
            var matches = schema
                .Members.Where(member =>
                    string.Equals(member.Name, path[index], StringComparison.OrdinalIgnoreCase)
                )
                .ToArray();
            if (matches.Length != 1)
            {
                throw new ArgumentException(
                    $"Mount path '{propertyPath}' has an unknown or ambiguous member '{path[index]}' in model '{schema.ModelType}'.",
                    nameof(propertyPath)
                );
            }

            var nested = matches[0].NestedSchemaFactory?.Invoke();
            if (nested is null)
            {
                throw new ArgumentException(
                    $"Mount path '{propertyPath}' continues through non-nested member '{matches[0].Name}'.",
                    nameof(propertyPath)
                );
            }

            return index == path.Count - 1
                ? nested
                : GetNestedSchema(nested, path, index + 1, propertyPath);
        }
    }
}

/// <summary>Configures one JSON file source in a model's source set.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
public sealed class JsonFileRegistration<TModel>
    where TModel : IConfiglueFacadeModel<TModel>
{
    private readonly JsonFileSourceOptions _options;
    private readonly ConfiglueSourceSetBuilder<TModel> _sources;

    internal JsonFileRegistration(
        JsonFileSourceOptions options,
        ConfiglueSourceSetBuilder<TModel> sources
    )
    {
        _options = options;
        _sources = sources;
    }

    /// <summary>Assigns a stable application-defined logical source name.</summary>
    public JsonFileRegistration<TModel> Named(string name)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _options.IdOverride = name;
        return this;
    }

    /// <summary>Assigns a stable logical source name using a key for this model.</summary>
    public JsonFileRegistration<TModel> Named(SourceKey<TModel> sourceKey) => Named(sourceKey.Name);

    /// <summary>Mounts the file's generated fragment at a strongly typed nested model member.</summary>
    public JsonFileRegistration<TModel> Mount<TSubtreeModel>(
        Expression<Func<TModel, TSubtreeModel?>> subtreeSelector
    )
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(subtreeSelector);
        if (_options.MountPath is not null)
        {
            throw new InvalidOperationException("A JSON file source can be mounted only once.");
        }

        _options.MountPath = GetPropertyPath(subtreeSelector);
        return this;
    }

    /// <summary>Selects a JSON document section for this file source.</summary>
    public JsonFileRegistration<TModel> Section(string sectionPath)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        _options.SectionPathOverride = sectionPath;
        return this;
    }

    /// <summary>Sets the read priority for this file source.</summary>
    public JsonFileRegistration<TModel> Priority(int priority)
    {
        EnsureMutable();
        _options.PriorityOverride = priority;
        return this;
    }

    /// <summary>Sets which read statuses allow resolution to fall back to lower-priority sources.</summary>
    public JsonFileRegistration<TModel> FallbackWhen(StateFallbackCondition condition)
    {
        EnsureMutable();
        if (
            (
                condition
                & ~(StateFallbackCondition.NotFoundOrUnavailable | StateFallbackCondition.Invalid)
            ) != 0
        )
        {
            throw new ArgumentOutOfRangeException(nameof(condition));
        }

        _options.FallbackConditionOverride = condition;
        return this;
    }

    /// <summary>Sets whether this file source is read-only.</summary>
    public JsonFileRegistration<TModel> ReadOnly(bool readOnly = true)
    {
        EnsureMutable();
        _options.ReadOnlyOverride = readOnly;
        return this;
    }

    /// <summary>Requires this file source to expose a writer.</summary>
    public JsonFileRegistration<TModel> Writable() => ReadOnly(false);

    /// <summary>Sets whether this file source watches for changes.</summary>
    public JsonFileRegistration<TModel> WatchChanges(bool watchChanges = true)
    {
        EnsureMutable();
        _options.WatchChangesOverride = watchChanges;
        return this;
    }

    /// <summary>Sets JSON serialization options for this file source.</summary>
    public JsonFileRegistration<TModel> SerializerOptions(JsonSerializerOptions serializerOptions)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(serializerOptions);
        _options.SerializerOptionsOverride = serializerOptions;
        return this;
    }

    /// <summary>Excludes this source from ordinary inferred write routing.</summary>
    public JsonFileRegistration<TModel> ExplicitOnly(bool explicitOnly = true)
    {
        EnsureMutable();
        _options.ExplicitOnlyOverride = explicitOnly;
        return this;
    }

    private static string GetPropertyPath<TSubtreeModel>(
        Expression<Func<TModel, TSubtreeModel?>> selector
    )
    {
        Expression expression = selector.Body;
        while (
            expression
                is UnaryExpression
                {
                    NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                } conversion
        )
        {
            expression = conversion.Operand;
        }

        var segments = new Stack<string>();
        while (expression is MemberExpression memberExpression)
        {
            if (memberExpression.Member.MemberType != MemberTypes.Property)
            {
                throw new ArgumentException(
                    "A mounted subtree selector must use generated model properties.",
                    nameof(selector)
                );
            }

            segments.Push(memberExpression.Member.Name);
            expression = memberExpression.Expression!;
        }

        if (expression != selector.Parameters[0] || segments.Count == 0)
        {
            throw new ArgumentException(
                "A mounted subtree selector must be a property path from its model parameter.",
                nameof(selector)
            );
        }

        return string.Join(".", segments);
    }

    private void EnsureMutable()
    {
        if (_sources.IsSealed)
        {
            throw new InvalidOperationException("The source registration has already been added.");
        }
    }
}
