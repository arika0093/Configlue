namespace Configlue;

/// <summary>Identifies the logical schema used to encode a state payload.</summary>
public readonly record struct StateSchemaMetadata(string? ModelId, int Version)
{
    /// <summary>The initial schema version.</summary>
    public const int InitialVersion = 1;
}
