namespace Configlue.Resources;

/// <summary>The bytes and metadata returned by a resource reader.</summary>
public readonly record struct ResourceReadResult
{
    /// <summary>Gets or initializes the <see cref="Status"/> value.</summary>
    public StateReadStatus Status { get; init; }

    /// <summary>Gets or initializes the <see cref="Content"/> value.</summary>
    public ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>Gets or initializes the <see cref="Revision"/> value.</summary>
    public string? Revision { get; init; }

    /// <summary>Gets or initializes the <see cref="Schema"/> value.</summary>
    public StateSchemaMetadata? Schema { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Status">The initial value for the <see cref="Status"/> property.</param>
    /// <param name="Content">The initial value for the <see cref="Content"/> property.</param>
    /// <param name="Revision">The initial value for the <see cref="Revision"/> property.</param>
    /// <param name="Schema">The initial value for the <see cref="Schema"/> property.</param>
    public ResourceReadResult(
        StateReadStatus Status,
        ReadOnlyMemory<byte> Content,
        string? Revision = null,
        StateSchemaMetadata? Schema = null
    )
    {
        if (Schema is { IsValid: false })
        {
            throw new ArgumentException(
                "Resource schema metadata must have a positive schema version.",
                nameof(Schema)
            );
        }
        this.Status = Status;
        this.Content = Content;
        this.Revision = Revision;
        this.Schema = Schema;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="Status">Receives the current <see cref="Status"/> value.</param>
    /// <param name="Content">Receives the current <see cref="Content"/> value.</param>
    /// <param name="Revision">Receives the current <see cref="Revision"/> value.</param>
    /// <param name="Schema">Receives the current <see cref="Schema"/> value.</param>
    public void Deconstruct(
        out StateReadStatus Status,
        out ReadOnlyMemory<byte> Content,
        out string? Revision,
        out StateSchemaMetadata? Schema
    )
    {
        Status = this.Status;
        Content = this.Content;
        Revision = this.Revision;
        Schema = this.Schema;
    }

    /// <summary>Creates a successful result.</summary>
    public static ResourceReadResult Success(
        ReadOnlyMemory<byte> content,
        string? revision = null,
        StateSchemaMetadata? schema = null
    ) => new(StateReadStatus.Success, content, revision, schema);

    /// <summary>Creates a missing-resource result.</summary>
    public static ResourceReadResult NotFound(string? revision = null) =>
        new(StateReadStatus.NotFound, default, revision);

    /// <summary>Creates a temporarily unavailable result.</summary>
    public static ResourceReadResult Unavailable(string? revision = null) =>
        new(StateReadStatus.Unavailable, default, revision);
}
