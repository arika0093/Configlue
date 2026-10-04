namespace Configlue.Resource.Gcs;

/// <summary>Object versioning and descriptive metadata preserved for revision and provenance.</summary>
/// <remarks>
/// Generations identify object content versions and back optimistic concurrency; the remaining
/// fields are informational snapshots captured with each read or write.
/// </remarks>
public sealed record GcsObjectMetadata(
    long? Generation,
    long? Metageneration,
    string? ETag,
    DateTimeOffset? Updated,
    string? ContentType,
    long? Size
);
