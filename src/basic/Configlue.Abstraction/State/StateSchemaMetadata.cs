namespace Configlue.State;

/// <summary>Identifies the logical schema used to encode a state payload.</summary>
public readonly record struct StateSchemaMetadata
{
    /// <summary>Gets or initializes the <see cref="ModelId"/> value.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets or initializes the <see cref="Version"/> value.</summary>
    public int Version { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="ModelId">The initial value for the <see cref="ModelId"/> property.</param>
    /// <param name="Version">The initial value for the <see cref="Version"/> property.</param>
    public StateSchemaMetadata(string? ModelId, int Version)
    {
        this.ModelId = ModelId;
        this.Version = Version;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="ModelId">Receives the current <see cref="ModelId"/> value.</param>
    /// <param name="Version">Receives the current <see cref="Version"/> value.</param>
    public void Deconstruct(out string? ModelId, out int Version)
    {
        ModelId = this.ModelId;
        Version = this.Version;
    }

    /// <summary>The initial schema version.</summary>
    public const int InitialVersion = 1;
}
