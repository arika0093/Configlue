namespace Configlue;

/// <summary>
/// Marks a partial type as a root Configlue model with its own persisted schema/state identity.
/// The generator also discovers undecorated structural POCOs reachable from a root and gives
/// them root-owned sparse fragment, deep merge, diff/patch, and deep-clone support without
/// requiring this attribute, <c>partial</c>, or a standalone schema id on every child type.
/// </summary>
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
