using System.Text.Json;
using System.Text.Json.Serialization;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using SharpYaml;

namespace Configlue.Source.Presets;

/// <summary>The built-in common source layers, ordered from lowest to highest precedence.</summary>
public enum CommonSourceLayer
{
    /// <summary>The per-user global file.</summary>
    Global,

    /// <summary>The current-directory local file.</summary>
    Local,

    /// <summary>The explicitly selected file.</summary>
    Explicit,

    /// <summary>Environment variables.</summary>
    Environment,

    /// <summary>Command-line arguments.</summary>
    Arguments,

    /// <summary>A read-only HTTP policy source.</summary>
    Http,
}

/// <summary>Builds common-source registrations and models within one configuration scope.</summary>
public sealed class CommonSourceBuilder
{
    private static readonly int[] LayerPriorities = [0, 100, 200, 300, 400, 500];
    private readonly ConfiglueBuilder _configlue;
    private readonly List<SourceDeclaration> _declarations = [];
    private readonly int[] _customCounts = new int[LayerPriorities.Length];
    private bool _hasAddedModel;
    private CommonSourceLayer? _defaultWriteLayer;

    internal CommonSourceBuilder(ConfiglueBuilder configlue)
    {
        _configlue = configlue;
    }

    /// <summary>Registers the per-user global file. The application ID selects its standard directory.</summary>
    public CommonFileSourceBuilder WithGlobal(
        string applicationId,
        string filename = "settings.json"
    )
    {
        EnsureDeclarationsMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ValidateFileName(filename);
        return AddFile(
            CommonSourceLayer.Global,
            CommonSource.Global.SourceId,
            Path.Combine(ConfiglueStandardPaths.GetStandardSaveDirectory(applicationId), filename)
        );
    }

    /// <summary>Registers a local file, relative to the current directory unless rooted.</summary>
    public CommonFileSourceBuilder WithLocal(string filename = "settings.json")
    {
        EnsureDeclarationsMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        return AddFile(
            CommonSourceLayer.Local,
            CommonSource.Local.SourceId,
            Path.GetFullPath(filename)
        );
    }

    /// <summary>Registers an explicitly selected file path.</summary>
    public CommonFileSourceBuilder WithExplicit(string filepath)
    {
        EnsureDeclarationsMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(filepath);
        return AddFile(
            CommonSourceLayer.Explicit,
            CommonSource.Specific.SourceId,
            Path.GetFullPath(filepath)
        );
    }

    /// <summary>Registers environment variables as a read-only source.</summary>
    public CommonEnvironmentSourceBuilder WithEnvironment(string prefix)
    {
        EnsureDeclarationsMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var declaration = AddDeclaration(CommonSourceLayer.Environment, static (_, _, _) => { });
        return new CommonEnvironmentSourceBuilder(declaration, prefix);
    }

    /// <summary>Selects which configured file layer receives ordinary writes.</summary>
    public CommonSourceBuilder DefaultWriteLayer(CommonSourceLayer layer)
    {
        EnsureDeclarationsMutable();
        if (
            layer
            is not (
                CommonSourceLayer.Global
                or CommonSourceLayer.Local
                or CommonSourceLayer.Explicit
            )
        )
        {
            throw new ArgumentOutOfRangeException(nameof(layer));
        }

        _defaultWriteLayer = layer;
        return this;
    }

    /// <summary>Registers a read-only JSON-over-HTTP source.</summary>
    public CommonHttpSourceBuilder WithHttpPolicy(
        string endpoint,
        HttpClient client,
        string id = "common.http"
    )
    {
        EnsureDeclarationsMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(client);
        var declaration = AddDeclaration(CommonSourceLayer.Http, static (_, _, _) => { });
        return new CommonHttpSourceBuilder(declaration, endpoint, client, id);
    }

    /// <summary>Registers a read-only JSON-over-HTTP source using a provider-aware client factory.</summary>
    public CommonHttpSourceBuilder WithHttpPolicy(
        string endpoint,
        Func<IServiceProvider?, HttpClient> clientFactory,
        string id = "common.http"
    )
    {
        EnsureDeclarationsMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(clientFactory);
        var declaration = AddDeclaration(CommonSourceLayer.Http, static (_, _, _) => { });
        return new CommonHttpSourceBuilder(declaration, endpoint, clientFactory, id);
    }

    /// <summary>Inserts one caller-defined source after the specified built-in layer.</summary>
    public CommonCustomSourceBuilder WithCustom(
        CommonSourceLayer afterLayer,
        Func<ConfiglueSourceSetBuilder, ConfiglueSourceRegistration> register
    )
    {
        EnsureDeclarationsMutable();
        ArgumentNullException.ThrowIfNull(register);
        var layerIndex = (int)afterLayer;
        if ((uint)layerIndex >= (uint)LayerPriorities.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(afterLayer));
        }

        var insertionLayer = layerIndex == LayerPriorities.Length - 1 ? layerIndex : layerIndex + 1;
        var customIndex = _customCounts[layerIndex]++;
        var priority = LayerPriorities[layerIndex] + customIndex + 1;
        if (insertionLayer < LayerPriorities.Length && priority >= LayerPriorities[insertionLayer])
        {
            throw new InvalidOperationException(
                $"Too many custom sources were inserted after '{afterLayer}'."
            );
        }

        var declaration = new SourceDeclaration(
            priority,
            null,
            (sources, selectedPriority, _) =>
            {
                register(sources).Priority(selectedPriority);
            }
        );
        _declarations.Add(declaration);
        return new CommonCustomSourceBuilder(declaration);
    }

    /// <summary>Adds a model using the common sources declared earlier in this scope.</summary>
    public void Add<TModel>(Action<ConfiglueModelBuilder<TModel>>? configure = null)
        where TModel : IConfiglueFacadeModel<TModel>
    {
        _hasAddedModel = true;
        var declarations = _declarations.ToArray();
        if (declarations.Length == 0)
        {
            throw new InvalidOperationException(
                "At least one common or custom source must be enabled before adding a model."
            );
        }
        var fileDeclarations = declarations
            .Where(static declaration => declaration.IsFile)
            .ToArray();
        var defaultWriteLayer = _defaultWriteLayer;
        if (
            defaultWriteLayer is null
            && fileDeclarations.Any(static declaration =>
                declaration.Layer == CommonSourceLayer.Explicit
            )
        )
        {
            defaultWriteLayer = CommonSourceLayer.Explicit;
        }
        else if (
            defaultWriteLayer is null
            && fileDeclarations.Any(static declaration =>
                declaration.Layer == CommonSourceLayer.Local
            )
        )
        {
            defaultWriteLayer = CommonSourceLayer.Local;
        }
        else if (
            defaultWriteLayer is null
            && fileDeclarations.Any(static declaration =>
                declaration.Layer == CommonSourceLayer.Global
            )
        )
        {
            defaultWriteLayer = CommonSourceLayer.Global;
        }
        if (
            _defaultWriteLayer is { } requestedLayer
            && !fileDeclarations.Any(declaration => declaration.Layer == requestedLayer)
        )
        {
            throw new InvalidOperationException(
                $"The default write layer '{requestedLayer}' was not configured."
            );
        }
        foreach (var declaration in declarations)
        {
            declaration.Seal();
        }

        _configlue.Add<TModel>(model =>
        {
            model.Sources(sources =>
            {
                foreach (var declaration in declarations.OrderByDescending(item => item.Priority))
                {
                    declaration.Register(
                        sources,
                        declaration.Priority,
                        declaration.Layer == defaultWriteLayer
                    );
                }
            });
            configure?.Invoke(model);
        });
    }

    internal void EnsureHasModel()
    {
        if (!_hasAddedModel)
        {
            throw new InvalidOperationException(
                "UseCommonSources must add at least one model in its configuration callback."
            );
        }
    }

    private CommonFileSourceBuilder AddFile(CommonSourceLayer layer, string id, string path)
    {
        var declaration = AddDeclaration(layer, static (_, _, _) => { }, isFile: true);
        var options = new CommonFileSourceBuilder(declaration, id, path);
        declaration.Register = (sources, priority, isDefaultWriteTarget) =>
            options.Register(sources, priority, isDefaultWriteTarget);
        return options;
    }

    private SourceDeclaration AddDeclaration(
        CommonSourceLayer layer,
        Action<ConfiglueSourceSetBuilder, int, bool> register,
        bool isFile = false
    )
    {
        var declaration = new SourceDeclaration(
            LayerPriorities[(int)layer],
            layer,
            register,
            isFile
        );
        _declarations.Add(declaration);
        return declaration;
    }

    private void EnsureDeclarationsMutable()
    {
        if (_hasAddedModel)
        {
            throw new InvalidOperationException(
                "Common source declarations must be completed before adding models."
            );
        }
    }

    private static void ValidateFileName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        const string invalidCharacters = "<>:\"/\\|?*";
        if (
            Path.IsPathRooted(value)
            || value is "." or ".."
            || value.IndexOfAny(invalidCharacters.ToCharArray()) >= 0
            || value.Any(char.IsControl)
        )
        {
            throw new ArgumentException(
                "The global file name must be a portable file name, not a path.",
                nameof(value)
            );
        }
    }

    internal sealed class SourceDeclaration(
        int priority,
        CommonSourceLayer? layer,
        Action<ConfiglueSourceSetBuilder, int, bool> register,
        bool isFile = false
    )
    {
        private bool _sealed;

        public int Priority { get; set; } = priority;
        public CommonSourceLayer? Layer { get; } = layer;
        public bool IsFile { get; } = isFile;
        public Action<ConfiglueSourceSetBuilder, int, bool> Register { get; set; } = register;

        public void Seal() => _sealed = true;

        public void EnsureMutable()
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    "A common source cannot be changed after it has been applied to a model."
                );
            }
        }
    }
}

/// <summary>Configures the provider and file options for one common file layer.</summary>
public sealed class CommonFileSourceBuilder
{
    private readonly CommonSourceBuilder.SourceDeclaration _declaration;
    private readonly string _id;
    private readonly string _path;
    private CommonFileFormat _format;
    private JsonSerializerOptions? _jsonOptions;
    private YamlSerializerOptions? _yamlOptions;
    private JsonNamingPolicy? _yamlNamingPolicy;
    private FileResourceOptions? _resourceOptions;
    private string? _section;
    private string? _schemaReferenceBaseUri;
    private bool _readOnly;
    private bool _watchChanges = true;
    private bool? _explicitOnly;
    private readonly List<IStateByteTransformer> _transformers = [];

    internal CommonFileSourceBuilder(
        CommonSourceBuilder.SourceDeclaration declaration,
        string id,
        string path
    )
    {
        _declaration = declaration;
        _id = id;
        _path = path;
    }

    /// <summary>Selects the JSON provider, which is the default for common files.</summary>
    public CommonFileSourceBuilder Json(JsonSerializerOptions? options = null)
    {
        EnsureMutable();
        _format = CommonFileFormat.Json;
        _jsonOptions = options;
        return this;
    }

    /// <summary>Selects JSON using generated metadata from a serializer context.</summary>
    public CommonFileSourceBuilder Json(JsonSerializerContext context)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(context);
        return Json(context.Options);
    }

    /// <summary>Selects the YAML provider.</summary>
    public CommonFileSourceBuilder Yaml(
        YamlSerializerOptions? serializerOptions = null,
        JsonNamingPolicy? propertyNamingPolicy = null
    )
    {
        EnsureMutable();
        _format = CommonFileFormat.Yaml;
        _yamlOptions = serializerOptions;
        _yamlNamingPolicy = propertyNamingPolicy;
        return this;
    }

    /// <summary>Selects the XML provider.</summary>
    public CommonFileSourceBuilder Xml()
    {
        EnsureMutable();
        _format = CommonFileFormat.Xml;
        return this;
    }

    /// <summary>Sets JSON serialization options.</summary>
    public CommonFileSourceBuilder SerializerOptions(JsonSerializerOptions options)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(options);
        _jsonOptions = options;
        return this;
    }

    /// <summary>Sets YAML serializer options.</summary>
    public CommonFileSourceBuilder YamlSerializerOptions(YamlSerializerOptions options)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(options);
        _yamlOptions = options;
        return this;
    }

    /// <summary>Sets the YAML property naming policy.</summary>
    public CommonFileSourceBuilder PropertyNamingPolicy(JsonNamingPolicy? policy)
    {
        EnsureMutable();
        _yamlNamingPolicy = policy;
        return this;
    }

    /// <summary>Sets the helper-created file resource options.</summary>
    public CommonFileSourceBuilder FileResourceOptions(FileResourceOptions options)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(options);
        _resourceOptions = options;
        return this;
    }

    /// <summary>Selects a document section for this file layer.</summary>
    public CommonFileSourceBuilder Section(string sectionPath)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        _section = sectionPath;
        return this;
    }

    /// <summary>Sets the JSON Schema reference written by JSON and YAML providers.</summary>
    public CommonFileSourceBuilder SchemaReferenceBaseUri(string schemaReferenceBaseUri)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaReferenceBaseUri);
        _schemaReferenceBaseUri = schemaReferenceBaseUri;
        return this;
    }

    /// <summary>Sets whether this file source is read-only.</summary>
    public CommonFileSourceBuilder ReadOnly(bool readOnly = true)
    {
        EnsureMutable();
        _readOnly = readOnly;
        return this;
    }

    /// <summary>Sets whether this file source watches for changes.</summary>
    public CommonFileSourceBuilder WatchChanges(bool watchChanges = true)
    {
        EnsureMutable();
        _watchChanges = watchChanges;
        return this;
    }

    /// <summary>Excludes this file source from inferred ordinary write routing.</summary>
    public CommonFileSourceBuilder ExplicitOnly(bool explicitOnly = true)
    {
        EnsureMutable();
        _explicitOnly = explicitOnly;
        return this;
    }

    /// <summary>Adds a byte transformer to this file source.</summary>
    public CommonFileSourceBuilder Transformer(IStateByteTransformer transformer)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(transformer);
        _transformers.Add(transformer);
        return this;
    }

    /// <summary>Overrides the fixed preset priority for this layer.</summary>
    public CommonFileSourceBuilder Priority(int priority)
    {
        EnsureMutable();
        _declaration.Priority = priority;
        return this;
    }

    internal void Register(
        ConfiglueSourceSetBuilder sources,
        int priority,
        bool isDefaultWriteTarget
    )
    {
        switch (_format)
        {
            case CommonFileFormat.Yaml:
                sources.FromYamlFile(
                    new YamlFileSourceOptions
                    {
                        Id = _id,
                        Path = _path,
                        Priority = priority,
                        SectionPath = _section,
                        SchemaReferenceBaseUri = _schemaReferenceBaseUri,
                        ReadOnly = _readOnly,
                        ExplicitOnly = _explicitOnly ?? !isDefaultWriteTarget,
                        WatchChanges = _watchChanges,
                        PropertyNamingPolicy = _yamlNamingPolicy,
                        SerializerOptions = _yamlOptions,
                        ResourceOptions = _resourceOptions,
                        Transformers = _transformers.ToArray(),
                    }
                );
                break;
            case CommonFileFormat.Xml:
                sources.FromXmlFile(
                    new XmlFileSourceOptions
                    {
                        Id = _id,
                        Path = _path,
                        Priority = priority,
                        SectionPath = _section,
                        ReadOnly = _readOnly,
                        ExplicitOnly = _explicitOnly ?? !isDefaultWriteTarget,
                        WatchChanges = _watchChanges,
                        ResourceOptions = _resourceOptions,
                        Transformers = _transformers.ToArray(),
                    }
                );
                break;
            default:
                sources.FromJsonFile(
                    new JsonFileSourceOptions
                    {
                        Id = _id,
                        Path = _path,
                        Priority = priority,
                        SectionPath = _section,
                        SchemaReferenceBaseUri = _schemaReferenceBaseUri,
                        ReadOnly = _readOnly,
                        ExplicitOnly = _explicitOnly ?? !isDefaultWriteTarget,
                        WatchChanges = _watchChanges,
                        SerializerOptions = _jsonOptions,
                        ResourceOptions = _resourceOptions,
                        Transformers = _transformers.ToArray(),
                    }
                );
                break;
        }
    }

    private void EnsureMutable() => _declaration.EnsureMutable();

    private enum CommonFileFormat
    {
        Json,
        Yaml,
        Xml,
    }
}
