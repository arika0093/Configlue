namespace Configlue;

/// <summary>The bytes to persist and the revision on which the write is based.</summary>
public readonly record struct ResourceWriteRequest(
    ReadOnlyMemory<byte> Content,
    string? ExpectedRevision = null,
    StateSchemaMetadata? Schema = null);
