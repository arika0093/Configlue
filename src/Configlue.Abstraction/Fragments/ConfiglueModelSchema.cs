namespace Configlue;

/// <summary>Describes one generated model member.</summary>
public readonly record struct ConfiglueMemberSchema(int Id, string Name, Type ValueType, MergeMode MergeMode);

/// <summary>Generated metadata for a model and its persisted schema.</summary>
public sealed class ConfiglueModelSchema
{
    /// <summary>Creates immutable model metadata.</summary>
    public ConfiglueModelSchema(
        Type modelType,
        string id,
        int version,
        IEnumerable<ConfiglueMemberSchema> members)
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
    }

    /// <summary>The CLR model type.</summary>
    public Type ModelType { get; }

    /// <summary>The stable persisted schema identifier.</summary>
    public string Id { get; }

    /// <summary>The persisted schema version.</summary>
    public int Version { get; }

    /// <summary>The members in generated stable order.</summary>
    public IReadOnlyList<ConfiglueMemberSchema> Members { get; }

    /// <summary>Returns the schema metadata to store alongside a snapshot.</summary>
    public StateSchemaMetadata ToMetadata() => new(Id, Version);
}
