namespace Configlue;

/// <summary>The revisions observed and written during a source-to-source migration.</summary>
public readonly record struct StateSourceMigrationResult(
    string SourceId,
    string TargetId,
    string? SourceRevision,
    string? TargetRevision
);
