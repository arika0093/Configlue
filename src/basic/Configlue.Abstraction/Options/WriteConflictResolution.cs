namespace Configlue;

/// <summary>Controls how edits are rebased when configuration values change concurrently.</summary>
public enum WriteConflictResolution
{
    /// <summary>Reject edits that conflict with changes made after the edit session began.</summary>
    FailOnConflict = 0,

    /// <summary>Prefer values changed by the edit session when the same members changed concurrently.</summary>
    LastWriteWins = 1,
}
