namespace Configlue;

/// <summary>The result of preparing one target in a retryable storage migration.</summary>
public readonly record struct StateStorageMigrationTargetResult(
    string TargetId,
    string? PreviousRevision,
    string? TargetRevision,
    bool WasAlreadyCurrent
);

/// <summary>The per-source revisions and per-target outcomes of a storage migration run.</summary>
public sealed class StateStorageMigrationResult
{
    /// <summary>Creates a storage migration result with immutable result collections.</summary>
    public StateStorageMigrationResult(
        IEnumerable<string> sourceIds,
        StateRevisionVector sourceRevisions,
        IEnumerable<StateStorageMigrationTargetResult> targets,
        IEnumerable<string>? retiredSourceIds = null
    )
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(sourceRevisions);
        ArgumentNullException.ThrowIfNull(targets);
        var sourceIdArray = sourceIds.ToArray();
        if (
            sourceIdArray.Any(string.IsNullOrWhiteSpace)
            || sourceIdArray.Distinct(StringComparer.Ordinal).Count() != sourceIdArray.Length
        )
        {
            throw new ArgumentException(
                "Source IDs must be non-empty and unique.",
                nameof(sourceIds)
            );
        }

        if (
            sourceRevisions.Revisions.Count != sourceIdArray.Length
            || sourceIdArray.Any(sourceId => !sourceRevisions.TryGetRevision(sourceId, out _))
        )
        {
            throw new ArgumentException(
                "The revision vector must contain exactly the selected sources.",
                nameof(sourceRevisions)
            );
        }

        var targetArray = targets.ToArray();
        if (
            targetArray.Length == 0
            || targetArray.Any(static target => string.IsNullOrWhiteSpace(target.TargetId))
            || targetArray
                .Select(static target => target.TargetId)
                .Distinct(StringComparer.Ordinal)
                .Count() != targetArray.Length
        )
        {
            throw new ArgumentException(
                "At least one target is required and target IDs must be non-empty and unique.",
                nameof(targets)
            );
        }

        var retiredIds = retiredSourceIds?.ToArray() ?? [];
        if (
            retiredIds.Any(string.IsNullOrWhiteSpace)
            || retiredIds.Distinct(StringComparer.Ordinal).Count() != retiredIds.Length
            || retiredIds.Any(sourceId => !sourceIdArray.Contains(sourceId, StringComparer.Ordinal))
        )
        {
            throw new ArgumentException(
                "Retired source IDs must be unique members of the selected sources.",
                nameof(retiredSourceIds)
            );
        }

        SourceIds = Array.AsReadOnly(sourceIdArray);
        SourceRevisions = sourceRevisions;
        Targets = Array.AsReadOnly(targetArray);
        RetiredSourceIds = Array.AsReadOnly(retiredIds);
    }

    /// <summary>The selected sources in read-priority order.</summary>
    public IReadOnlyList<string> SourceIds { get; }

    /// <summary>The revisions observed for selected sources.</summary>
    public StateRevisionVector SourceRevisions { get; }

    /// <summary>Per-target migration and verification outcomes.</summary>
    public IReadOnlyList<StateStorageMigrationTargetResult> Targets { get; }

    /// <summary>Selected sources excluded from this options instance after all targets were verified.</summary>
    public IReadOnlyList<string> RetiredSourceIds { get; }

    /// <summary>Whether this migration retired its selected sources from this options instance.</summary>
    public bool SourcesRetired => RetiredSourceIds.Count > 0;
}
