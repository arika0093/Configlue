namespace Configlue;

/// <summary>The result of writing state.</summary>
public readonly record struct StateWriteResult(string? Revision)
{
    /// <summary>Per-source outcomes when a configure session wrote to multiple sources.</summary>
    public StateMultiWriteResult? MultiWriteResult { get; init; }
}
