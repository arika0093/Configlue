namespace Configlue;

/// <summary>Describes one generated model member.</summary>
public readonly record struct ConfiglueMemberSchema(
    int Id,
    string Name,
    Type ValueType,
    MergeMode MergeMode,
    Func<object, object?>? GetValue = null,
    Func<ConfiglueModelSchema>? NestedSchemaFactory = null,
    Func<IEnumerable<object?>, object?>? CollectionValueFactory = null,
    string? EnvironmentVariableName = null
);

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
        Members = Array.AsReadOnly(members.ToArray());
        _emptyFragmentFactory = emptyFragmentFactory;
    }

    /// <summary>The CLR model type.</summary>
    public Type ModelType { get; }

    /// <summary>The stable persisted schema identifier.</summary>
    public string Id { get; }

    /// <summary>The persisted schema version.</summary>
    public int Version { get; }

    /// <summary>The members in generated stable order.</summary>
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
