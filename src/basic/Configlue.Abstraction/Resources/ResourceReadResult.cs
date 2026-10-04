namespace Configlue.Resources;

/// <summary>The bytes and metadata returned by a resource reader.</summary>
/// <remarks>Provider SPI: returned by resource implementations to the runtime.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct ResourceReadResult
{
    private readonly StateReadStatus? _status;

    /// <summary>The read outcome selected by a factory; the default value is <see cref="StateReadStatus.NotFound"/>.</summary>
    public StateReadStatus Status => _status ?? StateReadStatus.NotFound;

    /// <summary>The resource bytes, present only when <see cref="Status"/> is <see cref="StateReadStatus.Success"/>.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>The revision associated with the read, when provided by the resource.</summary>
    public string? Revision { get; }

    /// <summary>The schema metadata associated with a successful read.</summary>
    public StateSchemaMetadata? Schema { get; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Status">The initial value for the <see cref="Status"/> property.</param>
    /// <param name="Content">The initial value for the <see cref="Content"/> property.</param>
    /// <param name="Revision">The initial value for the <see cref="Revision"/> property.</param>
    /// <param name="Schema">The initial value for the <see cref="Schema"/> property.</param>
    private ResourceReadResult(
        StateReadStatus Status,
        ReadOnlyMemory<byte> Content,
        string? Revision = null,
        StateSchemaMetadata? Schema = null
    )
    {
        if (!Enum.IsDefined(typeof(StateReadStatus), Status))
        {
            throw new ArgumentOutOfRangeException(nameof(Status));
        }
        if (Schema is { IsValid: false })
        {
            throw new ArgumentException(
                "Resource schema metadata must have a positive schema version.",
                nameof(Schema)
            );
        }
        if (Status != StateReadStatus.Success && !Content.IsEmpty)
        {
            throw new ArgumentException(
                "Non-success resource results cannot contain content.",
                nameof(Content)
            );
        }
        _status = Status == StateReadStatus.NotFound ? null : Status;
        this.Content = Status == StateReadStatus.Success ? Content : default;
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

    /// <summary>Creates a result for a resource payload that was present but malformed or undecodable.</summary>
    public static ResourceReadResult InvalidPayload(string? revision = null) =>
        new(StateReadStatus.InvalidPayload, default, revision);
}
