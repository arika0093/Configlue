namespace Configlue;

/// <summary>Requests generated sparse-fragment support for a partial model.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ConfiglueModelAttribute : Attribute
{
    private int _version = StateSchemaMetadata.InitialVersion;

    /// <summary>Creates model metadata for a stable schema identifier.</summary>
    /// <param name="id">
    /// The stable persisted schema identifier. It must not be null, empty, or whitespace.
    /// </param>
    public ConfiglueModelAttribute(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    /// <summary>The stable persisted schema identifier.</summary>
    public string Id { get; }

    /// <summary>The persisted schema version of this model.</summary>
    public int Version
    {
        get => _version;
        set
        {
            if (value < StateSchemaMetadata.InitialVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _version = value;
        }
    }
}
