using System.Diagnostics.CodeAnalysis;

namespace Configlue.State;

/// <summary>A value returned from a state reader.</summary>
/// <remarks>Provider SPI result: returned by source implementations to the runtime.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct StateReadResult<T>
{
    private readonly StateReadStatus? _status;

    /// <summary>The read outcome selected by its factory.</summary>
    public StateReadStatus Status => _status ?? StateReadStatus.NotFound;

    /// <summary>The non-null successful value or optional malformed-payload value; missing and unavailable results carry no value.</summary>
    public T? Value { get; }

    /// <summary>Gets or initializes the <see cref="Revision"/> value.</summary>
    public string? Revision { get; init; }

    /// <summary>Gets or initializes the logical source registration identifier that produced this result.</summary>
    public SourceId? SourceId { get; init; }

    /// <summary>Gets or initializes human-readable physical location metadata for diagnostics; this is not a resource identity contract.</summary>
    public string? PhysicalOrigin { get; init; }

    /// <summary>Gets or initializes the <see cref="Schema"/> value.</summary>
    public StateSchemaMetadata? Schema { get; init; }

    /// <summary>Gets or initializes the <see cref="Revisions"/> value.</summary>
    public StateRevisionVector? Revisions { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Status">The initial value for the <see cref="Status"/> property.</param>
    /// <param name="Value">The initial value for the <see cref="Value"/> property.</param>
    /// <param name="Revision">The initial value for the <see cref="Revision"/> property.</param>
    /// <param name="SourceId">The initial value for the <see cref="SourceId"/> property.</param>
    /// <param name="PhysicalOrigin">The initial value for the <see cref="PhysicalOrigin"/> property.</param>
    /// <param name="Schema">The initial value for the <see cref="Schema"/> property.</param>
    /// <param name="Revisions">The initial value for the <see cref="Revisions"/> property.</param>
    private StateReadResult(
        StateReadStatus Status,
        T? Value,
        string? Revision = null,
        SourceId? SourceId = null,
        string? PhysicalOrigin = null,
        StateSchemaMetadata? Schema = null,
        StateRevisionVector? Revisions = null
    )
    {
        if (
            Status
            is not (
                StateReadStatus.Success
                or StateReadStatus.NotFound
                or StateReadStatus.Unavailable
                or StateReadStatus.InvalidPayload
            )
        )
        {
            throw new ArgumentOutOfRangeException(nameof(Status));
        }
        if (Schema is { IsValid: false })
        {
            throw new ArgumentException(
                "Read-result schema metadata must have a positive schema version.",
                nameof(Schema)
            );
        }
        if (Status == StateReadStatus.Success)
        {
            ArgumentNullException.ThrowIfNull(Value);
        }
        _status = Status == StateReadStatus.NotFound ? null : Status;
        this.Value = Status is StateReadStatus.NotFound or StateReadStatus.Unavailable
            ? default
            : Value;
        this.Revision = Revision;
        this.SourceId = SourceId;
        this.PhysicalOrigin = PhysicalOrigin;
        this.Schema = Schema;
        this.Revisions = Revisions;
    }

    internal static StateReadResult<T> Create(
        StateReadStatus Status,
        T? Value,
        string? Revision = null,
        SourceId? SourceId = null,
        string? PhysicalOrigin = null,
        StateSchemaMetadata? Schema = null,
        StateRevisionVector? Revisions = null
    ) =>
        new(
            Status,
            Status is StateReadStatus.NotFound or StateReadStatus.Unavailable ? default : Value,
            Revision,
            SourceId,
            PhysicalOrigin,
            Schema,
            Revisions
        );

    internal StateReadResult<T> WithValue(T value) =>
        new(Status, value, Revision, SourceId, PhysicalOrigin, Schema, Revisions);

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="Status">Receives the current <see cref="Status"/> value.</param>
    /// <param name="Value">Receives the current <see cref="Value"/> value.</param>
    /// <param name="Revision">Receives the current <see cref="Revision"/> value.</param>
    /// <param name="SourceId">Receives the current <see cref="SourceId"/> value.</param>
    /// <param name="PhysicalOrigin">Receives the current <see cref="PhysicalOrigin"/> value.</param>
    /// <param name="Schema">Receives the current <see cref="Schema"/> value.</param>
    /// <param name="Revisions">Receives the current <see cref="Revisions"/> value.</param>
    public void Deconstruct(
        out StateReadStatus Status,
        out T? Value,
        out string? Revision,
        out SourceId? SourceId,
        out string? PhysicalOrigin,
        out StateSchemaMetadata? Schema,
        out StateRevisionVector? Revisions
    )
    {
        Status = this.Status;
        Value = this.Value;
        Revision = this.Revision;
        SourceId = this.SourceId;
        PhysicalOrigin = this.PhysicalOrigin;
        Schema = this.Schema;
        Revisions = this.Revisions;
    }

    /// <summary>Creates a successful result with a non-null value.</summary>
    public static StateReadResult<T> Success(
        [DisallowNull] T value,
        string? revision = null,
        StateSchemaMetadata? schema = null
    ) => new(StateReadStatus.Success, value, revision, Schema: schema);

    /// <summary>Creates a missing-state result.</summary>
    public static StateReadResult<T> NotFound(string? revision = null) =>
        new(StateReadStatus.NotFound, default, revision);

    /// <summary>Creates a temporarily unavailable result.</summary>
    public static StateReadResult<T> Unavailable(string? revision = null) =>
        new(StateReadStatus.Unavailable, default, revision);

    /// <summary>
    /// Creates a result for a source-local malformed or undecodable payload. This is not an effective-model
    /// validation failure; those throw a validation exception.
    /// </summary>
    public static StateReadResult<T> InvalidPayload(T? value, string? revision = null) =>
        new(StateReadStatus.InvalidPayload, value, revision);

    /// <summary>
    /// Returns this result associated with its logical source. An existing physical origin is preserved;
    /// <paramref name="physicalOrigin"/> is used when the result does not already identify one.
    /// </summary>
    public StateReadResult<T> FromSource(SourceId sourceId, string? physicalOrigin = null) =>
        this with
        {
            SourceId = sourceId,
            PhysicalOrigin = PhysicalOrigin ?? physicalOrigin,
        };
}
