namespace Configlue.Migrations;

/// <summary>Durable progress for one declared storage migration.</summary>
public sealed class StateStorageMigrationProgress
{
    /// <summary>Creates a progress snapshot for a migration definition.</summary>
    public StateStorageMigrationProgress(
        string migrationId,
        IEnumerable<string> sourceIds,
        IEnumerable<string> targetSourceIds,
        IEnumerable<string>? completedTargetSourceIds = null,
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
            || sourceArray.Any(string.IsNullOrWhiteSpace)
            || sourceArray.Distinct(StringComparer.Ordinal).Count() != sourceArray.Length
        )
        {
            throw new ArgumentException(
                "Source IDs must be non-empty and unique.",
                nameof(sourceIds)
            );
        }

        if (
            targetArray.Length == 0
            || targetArray.Any(string.IsNullOrWhiteSpace)
            || targetArray.Distinct(StringComparer.Ordinal).Count() != targetArray.Length
        )
        {
            throw new ArgumentException(
                "Target IDs must be non-empty and unique.",
                nameof(targetSourceIds)
            );
        }

        if (
            completedArray.Any(string.IsNullOrWhiteSpace)
            || completedArray.Distinct(StringComparer.Ordinal).Count() != completedArray.Length
            || completedArray.Any(targetId =>
                !targetArray.Contains(targetId, StringComparer.Ordinal)
            )
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
    public IReadOnlyList<string> SourceIds { get; }

    /// <summary>The target IDs recorded when progress was saved.</summary>
    public IReadOnlyList<string> TargetSourceIds { get; }

    /// <summary>Targets whose writes and verification completed.</summary>
    public IReadOnlyList<string> CompletedTargetSourceIds { get; }

    /// <summary>Whether the migration definition requests source retirement.</summary>
    public bool RetireSources { get; }

    /// <summary>Whether every target completed and source retirement completed.</summary>
    public bool SourcesRetired { get; }

    /// <summary>Whether all targets were verified.</summary>
    public bool AllTargetsCompleted => CompletedTargetSourceIds.Count == TargetSourceIds.Count;
}
