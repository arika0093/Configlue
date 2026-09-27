namespace Configlue;

/// <summary>The source revisions observed during an explicit fallback promotion.</summary>
public readonly record struct StateFallbackPromotionResult
{
    /// <summary>Creates a fallback promotion result.</summary>
    public StateFallbackPromotionResult(
        string sourceId,
        string targetId,
        string? sourceRevision,
        string? targetRevision,
        bool wasAlreadyPromoted
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        SourceId = sourceId;
        TargetId = targetId;
        SourceRevision = sourceRevision;
        TargetRevision = targetRevision;
        WasAlreadyPromoted = wasAlreadyPromoted;
    }

    /// <summary>The candidate that supplied the state copied during promotion.</summary>
    public string SourceId { get; }

    /// <summary>The candidate selected to receive the state.</summary>
    public string TargetId { get; }

    /// <summary>The source revision observed before the target write.</summary>
    public string? SourceRevision { get; }

    /// <summary>The target revision after promotion, when the writer exposes one.</summary>
    public string? TargetRevision { get; }

    /// <summary>Whether the target was already the selected candidate, so no write was needed.</summary>
    public bool WasAlreadyPromoted { get; }
}
