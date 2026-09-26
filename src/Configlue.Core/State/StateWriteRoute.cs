namespace Configlue;

/// <summary>Selects the source that receives an ordinary state write.</summary>
public readonly record struct StateWriteRoute(string? SourceId = null)
{
    /// <summary>Uses the highest-priority source that has a writer.</summary>
    public static StateWriteRoute HighestPriorityWritable => default;

    /// <summary>Selects a source by its logical identifier.</summary>
    public static StateWriteRoute To(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return new StateWriteRoute(sourceId);
    }
}
