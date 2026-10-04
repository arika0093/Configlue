namespace Configlue.Migrations;

/// <summary>The result of preparing one target in a retryable storage migration.</summary>
/// <remarks>Advanced migration result.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct StateStorageMigrationTargetResult
{
    /// <summary>Gets or initializes the <see cref="TargetId"/> value.</summary>
    public SourceId TargetId { get; init; }

    /// <summary>Gets or initializes the <see cref="PreviousRevision"/> value.</summary>
    public string? PreviousRevision { get; init; }

    /// <summary>Gets or initializes the <see cref="TargetRevision"/> value.</summary>
    public string? TargetRevision { get; init; }

    /// <summary>Gets or initializes the <see cref="WasAlreadyCurrent"/> value.</summary>
    public bool WasAlreadyCurrent { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="TargetId">The initial value for the <see cref="TargetId"/> property.</param>
    /// <param name="PreviousRevision">The initial value for the <see cref="PreviousRevision"/> property.</param>
    /// <param name="TargetRevision">The initial value for the <see cref="TargetRevision"/> property.</param>
    /// <param name="WasAlreadyCurrent">The initial value for the <see cref="WasAlreadyCurrent"/> property.</param>
    public StateStorageMigrationTargetResult(
        SourceId TargetId,
        string? PreviousRevision,
        string? TargetRevision,
        bool WasAlreadyCurrent
    )
    {
        this.TargetId = TargetId;
        this.PreviousRevision = PreviousRevision;
        this.TargetRevision = TargetRevision;
        this.WasAlreadyCurrent = WasAlreadyCurrent;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="TargetId">Receives the current <see cref="TargetId"/> value.</param>
    /// <param name="PreviousRevision">Receives the current <see cref="PreviousRevision"/> value.</param>
    /// <param name="TargetRevision">Receives the current <see cref="TargetRevision"/> value.</param>
    /// <param name="WasAlreadyCurrent">Receives the current <see cref="WasAlreadyCurrent"/> value.</param>
    public void Deconstruct(
        out SourceId TargetId,
        out string? PreviousRevision,
        out string? TargetRevision,
        out bool WasAlreadyCurrent
    )
    {
        TargetId = this.TargetId;
        PreviousRevision = this.PreviousRevision;
        TargetRevision = this.TargetRevision;
        WasAlreadyCurrent = this.WasAlreadyCurrent;
    }
}

/// <summary>The per-source revisions and per-target outcomes of a storage migration run.</summary>
/// <remarks>Advanced migration result.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class StateStorageMigrationResult
{
    /// <summary>Creates a storage migration result with immutable result collections.</summary>
    public StateStorageMigrationResult(
        IEnumerable<SourceId> sourceIds,
        StateRevisionVector sourceRevisions,
        IEnumerable<StateStorageMigrationTargetResult> targets,
        IEnumerable<SourceId>? retiredSourceIds = null
    )
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(sourceRevisions);
        ArgumentNullException.ThrowIfNull(targets);
        var sourceIdArray = sourceIds.ToArray();
        if (
            sourceIdArray.Any(static sourceId => sourceId.IsDefault)
            || sourceIdArray.Distinct().Count() != sourceIdArray.Length
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
            || targetArray.Any(static target => target.TargetId.IsDefault)
            || targetArray.Select(static target => target.TargetId).Distinct().Count()
                != targetArray.Length
        )
        {
            throw new ArgumentException(
                "At least one target is required and target IDs must be non-empty and unique.",
                nameof(targets)
            );
        }

        var retiredIds = retiredSourceIds?.ToArray() ?? [];
        if (
            retiredIds.Any(static sourceId => sourceId.IsDefault)
            || retiredIds.Distinct().Count() != retiredIds.Length
            || retiredIds.Any(sourceId => !sourceIdArray.Contains(sourceId))
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
    public IReadOnlyList<SourceId> SourceIds { get; }

    /// <summary>The revisions observed for selected sources.</summary>
    public StateRevisionVector SourceRevisions { get; }

    /// <summary>Per-target migration and verification outcomes.</summary>
    public IReadOnlyList<StateStorageMigrationTargetResult> Targets { get; }

    /// <summary>Selected sources excluded from this state instance after all targets were verified.</summary>
    public IReadOnlyList<SourceId> RetiredSourceIds { get; }

    /// <summary>Whether this migration retired its selected sources from this state instance.</summary>
    public bool SourcesRetired => RetiredSourceIds.Count > 0;
}
