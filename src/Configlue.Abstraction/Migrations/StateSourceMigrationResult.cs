namespace Configlue;

/// <summary>The revisions observed and written during a source-to-source migration.</summary>
public readonly record struct StateSourceMigrationResult
{
    /// <summary>Gets or initializes the <see cref="SourceId"/> value.</summary>
    public string SourceId { get; init; }

    /// <summary>Gets or initializes the <see cref="TargetId"/> value.</summary>
    public string TargetId { get; init; }

    /// <summary>Gets or initializes the <see cref="SourceRevision"/> value.</summary>
    public string? SourceRevision { get; init; }

    /// <summary>Gets or initializes the <see cref="TargetRevision"/> value.</summary>
    public string? TargetRevision { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="SourceId">The initial value for the <see cref="SourceId"/> property.</param>
    /// <param name="TargetId">The initial value for the <see cref="TargetId"/> property.</param>
    /// <param name="SourceRevision">The initial value for the <see cref="SourceRevision"/> property.</param>
    /// <param name="TargetRevision">The initial value for the <see cref="TargetRevision"/> property.</param>
    public StateSourceMigrationResult(
        string SourceId,
        string TargetId,
        string? SourceRevision,
        string? TargetRevision
    )
    {
        this.SourceId = SourceId;
        this.TargetId = TargetId;
        this.SourceRevision = SourceRevision;
        this.TargetRevision = TargetRevision;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="SourceId">Receives the current <see cref="SourceId"/> value.</param>
    /// <param name="TargetId">Receives the current <see cref="TargetId"/> value.</param>
    /// <param name="SourceRevision">Receives the current <see cref="SourceRevision"/> value.</param>
    /// <param name="TargetRevision">Receives the current <see cref="TargetRevision"/> value.</param>
    public void Deconstruct(
        out string SourceId,
        out string TargetId,
        out string? SourceRevision,
        out string? TargetRevision
    )
    {
        SourceId = this.SourceId;
        TargetId = this.TargetId;
        SourceRevision = this.SourceRevision;
        TargetRevision = this.TargetRevision;
    }
}
