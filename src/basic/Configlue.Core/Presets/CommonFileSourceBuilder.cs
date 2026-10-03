namespace Configlue.Source.Presets;

/// <summary>Configures the provider and file options for one common file layer.</summary>
public sealed class CommonFileSourceBuilder
{
    private readonly Action _ensureMutable;
    private readonly Action<int> _setPriority;
    private readonly string _id;
    private readonly string _path;
    private readonly Dictionary<Type, object> _providerOptions = [];
    private Action<ConfiglueSourceSetBuilder, CommonFileSourceSettings>? _providerRegistration;
    private FileResourceOptions? _resourceOptions;
    private string? _section;
    private string? _schemaReferenceBaseUri;
    private bool _readOnly;
    private bool _watchChanges = true;
    private bool? _explicitOnly;
    private readonly List<IStateByteTransformer> _transformers = [];

    /// <summary>Creates a common file source builder for a provider-neutral preset.</summary>
    /// <param name="ensureMutable">Checks that the owning preset has not been applied yet.</param>
    /// <param name="setPriority">Updates the owning preset's layer priority.</param>
    /// <param name="id">The logical source identifier.</param>
    /// <param name="path">The resolved file path.</param>
    public CommonFileSourceBuilder(
        Action ensureMutable,
        Action<int> setPriority,
        string id,
        string path
    )
    {
        ArgumentNullException.ThrowIfNull(ensureMutable);
        ArgumentNullException.ThrowIfNull(setPriority);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _ensureMutable = ensureMutable;
        _setPriority = setPriority;
        _id = id;
        _path = path;
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

    /// <summary>Sets the schema reference base URI used by providers that support schema metadata.</summary>
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
        if (
            transformer is not ISynchronousStateByteTransformer
            && transformer is not IAsyncStateByteTransformer
        )
        {
            throw new ArgumentException(
                $"Transformer type '{transformer.GetType()}' must implement a synchronous or asynchronous transformer capability.",
                nameof(transformer)
            );
        }
        _transformers.Add(transformer);
        return this;
    }

    /// <summary>Overrides the fixed preset priority for this layer.</summary>
    public CommonFileSourceBuilder Priority(int priority)
    {
        EnsureMutable();
        _setPriority(priority);
        return this;
    }

    /// <summary>Registers this configured file source in a source set.</summary>
    /// <param name="sources">The source set being configured.</param>
    /// <param name="priority">The resolved priority for this source.</param>
    /// <param name="isDefaultWriteTarget">Whether this source is the default write target.</param>
    public void Register(ConfiglueSourceSetBuilder sources, int priority, bool isDefaultWriteTarget)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var registration =
            _providerRegistration
            ?? throw new InvalidOperationException(
                "No file provider is selected for this common source layer."
            );
        registration(
            sources,
            new CommonFileSourceSettings
            {
                Id = _id,
                Path = _path,
                Priority = priority,
                SectionPath = _section,
                SchemaReferenceBaseUri = _schemaReferenceBaseUri,
                ReadOnly = _readOnly,
                ExplicitOnly = _explicitOnly ?? !isDefaultWriteTarget,
                WatchChanges = _watchChanges,
                ResourceOptions = _resourceOptions,
                Transformers = _transformers.ToArray(),
            }
        );
    }

    /// <summary>Gets or creates extension-specific options associated with this file source.</summary>
    public TOptions GetOrCreateProviderOptions<TOptions>(Func<TOptions> create)
        where TOptions : class
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(create);
        if (_providerOptions.TryGetValue(typeof(TOptions), out var options))
        {
            return (TOptions)options;
        }

        var newOptions = create();
        _providerOptions.Add(typeof(TOptions), newOptions);
        return newOptions;
    }

    /// <summary>Sets how a provider extension registers this configured file source.</summary>
    public void SetProviderRegistration<TOptions>(
        TOptions options,
        Action<ConfiglueSourceSetBuilder, CommonFileSourceSettings, TOptions> register
    )
        where TOptions : class
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(register);
        _providerRegistration = (sources, settings) => register(sources, settings, options);
    }

    /// <summary>Sets how a provider extension registers this configured file source.</summary>
    public void SetProviderRegistration(
        Action<ConfiglueSourceSetBuilder, CommonFileSourceSettings> register
    )
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(register);
        _providerRegistration = register;
    }

    private void EnsureMutable() => _ensureMutable();
}

/// <summary>Describes the common file settings passed to a file-provider registration.</summary>
public sealed record CommonFileSourceSettings
{
    /// <summary>The logical source identifier.</summary>
    public required string Id { get; init; }

    /// <summary>The fully resolved file path.</summary>
    public required string Path { get; init; }

    /// <summary>The source priority.</summary>
    public int Priority { get; init; }

    /// <summary>The optional provider-specific document section.</summary>
    public string? SectionPath { get; init; }

    /// <summary>The optional schema reference base URI.</summary>
    public string? SchemaReferenceBaseUri { get; init; }

    /// <summary>Whether the source is read-only.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Whether writes require explicit source selection.</summary>
    public bool ExplicitOnly { get; init; }

    /// <summary>Whether the source watches for external changes.</summary>
    public bool WatchChanges { get; init; }

    /// <summary>Options for the underlying file resource.</summary>
    public FileResourceOptions? ResourceOptions { get; init; }

    /// <summary>Transformers applied to this source.</summary>
    public IReadOnlyList<IStateByteTransformer> Transformers { get; init; } = [];
}
