namespace Configlue;

/// <summary>The bytes and metadata returned by a resource reader.</summary>
public readonly record struct ResourceReadResult(
    StateReadStatus Status,
    ReadOnlyMemory<byte> Content,
    string? Revision = null,
    StateSchemaMetadata? Schema = null)
{
    /// <summary>Creates a successful result.</summary>
    public static ResourceReadResult Success(
        ReadOnlyMemory<byte> content,
        string? revision = null,
        StateSchemaMetadata? schema = null) =>
        new(StateReadStatus.Success, content, revision, schema);

    /// <summary>Creates a missing-resource result.</summary>
    public static ResourceReadResult NotFound(string? revision = null) =>
        new(StateReadStatus.NotFound, default, revision);

    /// <summary>Creates a temporarily unavailable result.</summary>
    public static ResourceReadResult Unavailable(string? revision = null) =>
        new(StateReadStatus.Unavailable, default, revision);
}
