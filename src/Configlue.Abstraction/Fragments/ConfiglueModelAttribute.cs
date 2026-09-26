namespace Configlue;

/// <summary>Requests generated sparse-fragment support for a partial model.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ConfiglueModelAttribute : Attribute
{
    /// <summary>Creates model metadata with the initial schema version.</summary>
    public ConfiglueModelAttribute(int version = StateSchemaMetadata.InitialVersion)
    {
        Version = version;
    }

    /// <summary>The persisted schema version of this model.</summary>
    public int Version { get; }

    /// <summary>An optional stable schema identifier.</summary>
    public string? Id { get; set; }
}
