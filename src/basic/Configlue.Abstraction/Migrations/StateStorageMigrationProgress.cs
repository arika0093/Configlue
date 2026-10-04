namespace Configlue.Migrations;

/// <summary>Durable progress for one declared storage migration.</summary>
/// <remarks>Advanced application API for storage evolution.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class StateStorageMigrationProgress
{
    /// <summary>Creates a progress snapshot for a migration definition.</summary>
    public StateStorageMigrationProgress(
        string migrationId,
        IEnumerable<SourceId> sourceIds,
        IEnumerable<SourceId> targetSourceIds,
        IEnumerable<SourceId>? completedTargetSourceIds = null,
        bool retireSources = false,
        bool sourcesRetired = false
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(targetSourceIds);
        var sourceArray = sourceIds.ToArray();
        var targetArray = targetSourceIds.ToArray();
        var completedArray = completedTargetSourceIds?.ToArray() ?? [];
        if (
            sourceArray.Length == 0
            || sourceArray.Any(static sourceId => sourceId.IsDefault)
            || sourceArray.Distinct().Count() != sourceArray.Length
        )
        {
            throw new ArgumentException(
                "Source IDs must be non-empty and unique.",
                nameof(sourceIds)
            );
        }

        if (
            targetArray.Length == 0
            || targetArray.Any(static sourceId => sourceId.IsDefault)
            || targetArray.Distinct().Count() != targetArray.Length
        )
        {
            throw new ArgumentException(
                "Target IDs must be non-empty and unique.",
                nameof(targetSourceIds)
            );
        }

        if (
            completedArray.Any(static sourceId => sourceId.IsDefault)
            || completedArray.Distinct().Count() != completedArray.Length
            || completedArray.Any(targetId => !targetArray.Contains(targetId))
            || (sourcesRetired && (!retireSources || completedArray.Length != targetArray.Length))
        )
        {
            throw new ArgumentException(
                "Completed target IDs must be unique members of the migration targets, and all targets must be complete before source retirement.",
                nameof(completedTargetSourceIds)
            );
        }

        MigrationId = migrationId;
        SourceIds = Array.AsReadOnly(sourceArray);
        TargetSourceIds = Array.AsReadOnly(targetArray);
        CompletedTargetSourceIds = Array.AsReadOnly(completedArray);
        RetireSources = retireSources;
        SourcesRetired = sourcesRetired;
    }

    /// <summary>The stable migration definition ID.</summary>
    public string MigrationId { get; }

    /// <summary>The selected source IDs recorded when progress was saved.</summary>
    public IReadOnlyList<SourceId> SourceIds { get; }

    /// <summary>The target IDs recorded when progress was saved.</summary>
    public IReadOnlyList<SourceId> TargetSourceIds { get; }

    /// <summary>Targets whose writes and verification completed.</summary>
    public IReadOnlyList<SourceId> CompletedTargetSourceIds { get; }

    /// <summary>Whether the migration definition requests source retirement.</summary>
    public bool RetireSources { get; }

    /// <summary>Whether every target completed and source retirement completed.</summary>
    public bool SourcesRetired { get; }

    /// <summary>Whether all targets were verified.</summary>
    public bool AllTargetsCompleted => CompletedTargetSourceIds.Count == TargetSourceIds.Count;
}
