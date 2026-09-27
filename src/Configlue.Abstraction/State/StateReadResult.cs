namespace Configlue;

/// <summary>A value returned from a state reader.</summary>
public readonly record struct StateReadResult<T>(
    StateReadStatus Status,
    T? Value,
    string? Revision = null,
    string? SourceId = null,
    string? PhysicalOrigin = null,
    StateSchemaMetadata? Schema = null,
    StateRevisionVector? Revisions = null
)
{
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
