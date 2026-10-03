namespace Configlue;

/// <summary>Describes one generated model member.</summary>
/// <remarks>The default value is uninitialized; its name and value type expose safe sentinel values.</remarks>
public readonly record struct ConfiglueMemberSchema
{
    private readonly string? _name;
    private readonly Type? _valueType;

    /// <summary>
    /// Gets or initializes this member's generated ordinal within one model schema version.
    /// </summary>
    /// <remarks>
    /// The ordinal is scoped to the exact model type, schema identifier, and version described by
    /// its <see cref="ConfiglueModelSchema"/>. Generated ordinals can shift when readable members
    /// are added, removed, or renamed. Do not persist an ordinal or compare it across schema
    /// versions; use member names and schema-version-aware migration instead.
    /// </remarks>
    public int Id { get; init; }

    /// <summary>Gets or initializes the <see cref="Name"/> value.</summary>
    public string Name
    {
        get => _name ?? string.Empty;
        init => _name = value;
    }

    /// <summary>Gets or initializes the <see cref="ValueType"/> value.</summary>
    public Type ValueType
    {
        get => _valueType ?? typeof(void);
        init => _valueType = value;
    }

    /// <summary>Whether this value is the uninitialized default member metadata.</summary>
    public bool IsDefault =>
        string.IsNullOrWhiteSpace(_name) || _valueType is null || _valueType == typeof(void);

    /// <summary>Gets or initializes the <see cref="MergeMode"/> value.</summary>
    public MergeMode MergeMode { get; init; }

    /// <summary>Gets or initializes the <see cref="GetValue"/> value.</summary>
    public Func<object, object?>? GetValue { get; init; }

    /// <summary>Gets or initializes the <see cref="NestedSchemaFactory"/> value.</summary>
    public Func<ConfiglueModelSchema>? NestedSchemaFactory { get; init; }

    /// <summary>Gets or initializes the <see cref="CollectionValueFactory"/> value.</summary>
    public Func<IEnumerable<object?>, object?>? CollectionValueFactory { get; init; }

    /// <summary>Gets or initializes the <see cref="EnvironmentVariableName"/> value.</summary>
    public string? EnvironmentVariableName { get; init; }

    /// <summary>Gets or initializes the <see cref="MergeStrategy"/> value.</summary>
    public IConfiglueMergeStrategy? MergeStrategy { get; init; }

    /// <summary>Creates a boxed default using generated code, without reflection.</summary>
    public Func<object?>? DefaultValueFactory { get; init; }

    /// <summary>Tests element membership using the declared collection's comparer semantics.</summary>
    public Func<object, object?, bool>? ContainsElement { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Id">The initial value for the <see cref="Id"/> property.</param>
    /// <param name="Name">The initial value for the <see cref="Name"/> property.</param>
    /// <param name="ValueType">The initial value for the <see cref="ValueType"/> property.</param>
    /// <param name="MergeMode">The initial value for the <see cref="MergeMode"/> property.</param>
    /// <param name="GetValue">The initial value for the <see cref="GetValue"/> property.</param>
    /// <param name="NestedSchemaFactory">The initial value for the <see cref="NestedSchemaFactory"/> property.</param>
    /// <param name="CollectionValueFactory">The initial value for the <see cref="CollectionValueFactory"/> property.</param>
    /// <param name="EnvironmentVariableName">The initial value for the <see cref="EnvironmentVariableName"/> property.</param>
    /// <param name="MergeStrategy">The initial value for the <see cref="MergeStrategy"/> property.</param>
    /// <param name="DefaultValueFactory">The generated factory for the member's default.</param>
    public ConfiglueMemberSchema(
        int Id,
        string Name,
        Type ValueType,
        MergeMode MergeMode,
        Func<object, object?>? GetValue = null,
        Func<ConfiglueModelSchema>? NestedSchemaFactory = null,
        Func<IEnumerable<object?>, object?>? CollectionValueFactory = null,
        string? EnvironmentVariableName = null,
        IConfiglueMergeStrategy? MergeStrategy = null,
        Func<object?>? DefaultValueFactory = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentNullException.ThrowIfNull(ValueType);
        if (ValueType == typeof(void))
        {
            throw new ArgumentException("A model member cannot have type void.", nameof(ValueType));
        }
        this.Id = Id;
        this.Name = Name;
        this.ValueType = ValueType;
        this.MergeMode = MergeMode;
        this.GetValue = GetValue;
        this.NestedSchemaFactory = NestedSchemaFactory;
        this.CollectionValueFactory = CollectionValueFactory;
        this.EnvironmentVariableName = EnvironmentVariableName;
        this.MergeStrategy = MergeStrategy;
        this.DefaultValueFactory = DefaultValueFactory;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="Id">Receives the current <see cref="Id"/> value.</param>
    /// <param name="Name">Receives the current <see cref="Name"/> value.</param>
    /// <param name="ValueType">Receives the current <see cref="ValueType"/> value.</param>
    /// <param name="MergeMode">Receives the current <see cref="MergeMode"/> value.</param>
    /// <param name="GetValue">Receives the current <see cref="GetValue"/> value.</param>
    /// <param name="NestedSchemaFactory">Receives the current <see cref="NestedSchemaFactory"/> value.</param>
    /// <param name="CollectionValueFactory">Receives the current <see cref="CollectionValueFactory"/> value.</param>
    /// <param name="EnvironmentVariableName">Receives the current <see cref="EnvironmentVariableName"/> value.</param>
    /// <param name="MergeStrategy">Receives the current <see cref="MergeStrategy"/> value.</param>
    public void Deconstruct(
        out int Id,
        out string Name,
        out Type ValueType,
        out MergeMode MergeMode,
        out Func<object, object?>? GetValue,
        out Func<ConfiglueModelSchema>? NestedSchemaFactory,
        out Func<IEnumerable<object?>, object?>? CollectionValueFactory,
        out string? EnvironmentVariableName,
        out IConfiglueMergeStrategy? MergeStrategy
    )
    {
        Id = this.Id;
        Name = this.Name;
        ValueType = this.ValueType;
        MergeMode = this.MergeMode;
        GetValue = this.GetValue;
        NestedSchemaFactory = this.NestedSchemaFactory;
        CollectionValueFactory = this.CollectionValueFactory;
        EnvironmentVariableName = this.EnvironmentVariableName;
        MergeStrategy = this.MergeStrategy;
    }
}

/// <summary>Generated metadata for a model and its persisted schema.</summary>
public sealed class ConfiglueModelSchema
{
    private readonly Func<IConfiglueFragment>? _emptyFragmentFactory;

    /// <summary>Creates immutable model metadata.</summary>
    public ConfiglueModelSchema(
        Type modelType,
        string id,
        int version,
        IEnumerable<ConfiglueMemberSchema> members,
        Func<IConfiglueFragment>? emptyFragmentFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(modelType);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(members);
        if (version < StateSchemaMetadata.InitialVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        ModelType = modelType;
        Id = id;
        Version = version;
        var memberArray = members.ToArray();
        if (memberArray.Any(static member => member.IsDefault))
        {
            throw new ArgumentException(
                "Model schema members cannot contain uninitialized metadata.",
                nameof(members)
            );
        }

        Members = Array.AsReadOnly(memberArray);
        _emptyFragmentFactory = emptyFragmentFactory;
    }

    /// <summary>The CLR model type.</summary>
    public Type ModelType { get; }

    /// <summary>The stable persisted schema identifier.</summary>
    public string Id { get; }

    /// <summary>The persisted schema version.</summary>
    public int Version { get; }

    /// <summary>The members in generated ordinal order for this schema version.</summary>
    /// <remarks>
    /// Member IDs are local to this model type, schema ID, and version. They may change when members
    /// are added, removed, or renamed in another schema version; do not persist them or compare them
    /// across versions.
    /// </remarks>
    public IReadOnlyList<ConfiglueMemberSchema> Members { get; }

    /// <summary>Creates an empty generated fragment for this model schema.</summary>
    public IConfiglueFragment CreateEmptyFragment() =>
        _emptyFragmentFactory?.Invoke()
        ?? throw new InvalidOperationException(
            $"Schema '{Id}' does not provide an empty fragment factory."
        );

    /// <summary>Returns the schema metadata to store alongside a snapshot.</summary>
    public StateSchemaMetadata ToMetadata() => new(Id, Version);
}
