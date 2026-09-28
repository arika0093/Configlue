namespace Configlue.State;

/// <summary>A value returned from a state reader.</summary>
public readonly record struct StateReadResult<T>
{
    /// <summary>Gets or initializes the <see cref="Status"/> value.</summary>
    public StateReadStatus Status { get; init; }

    /// <summary>Gets or initializes the <see cref="Value"/> value.</summary>
    public T? Value { get; init; }

    /// <summary>Gets or initializes the <see cref="Revision"/> value.</summary>
    public string? Revision { get; init; }

    /// <summary>Gets or initializes the <see cref="SourceId"/> value.</summary>
    public string? SourceId { get; init; }

    /// <summary>Gets or initializes the <see cref="PhysicalOrigin"/> value.</summary>
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
    public StateReadResult(
        StateReadStatus Status,
        T? Value,
        string? Revision = null,
        string? SourceId = null,
        string? PhysicalOrigin = null,
        StateSchemaMetadata? Schema = null,
        StateRevisionVector? Revisions = null
    )
    {
        this.Status = Status;
        this.Value = Value;
        this.Revision = Revision;
        this.SourceId = SourceId;
        this.PhysicalOrigin = PhysicalOrigin;
        this.Schema = Schema;
        this.Revisions = Revisions;
    }

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
        out string? SourceId,
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

    /// <summary>Creates a successful result.</summary>
    public static StateReadResult<T> Success(
        T? value,
        string? revision = null,
        StateSchemaMetadata? schema = null
    ) => new(StateReadStatus.Success, value, revision, Schema: schema);

    /// <summary>Creates a missing-state result.</summary>
    public static StateReadResult<T> NotFound(string? revision = null) =>
        new(StateReadStatus.NotFound, default, revision);

    /// <summary>Creates a temporarily unavailable result.</summary>
    public static StateReadResult<T> Unavailable(string? revision = null) =>
        new(StateReadStatus.Unavailable, default, revision);

    /// <summary>Creates a result for a value that failed validation.</summary>
    public static StateReadResult<T> Invalid(T? value, string? revision = null) =>
        new(StateReadStatus.Invalid, value, revision);

    /// <summary>
    /// Returns this result associated with its logical source. An existing physical origin is preserved;
    /// <paramref name="physicalOrigin"/> is used when the result does not already identify one.
    /// </summary>
    public StateReadResult<T> FromSource(string sourceId, string? physicalOrigin = null) =>
        this with
        {
            SourceId = sourceId,
            PhysicalOrigin = PhysicalOrigin ?? physicalOrigin,
        };
}
