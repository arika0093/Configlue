namespace Configlue;

/// <summary>A multi-source write failed after one or more physical writes had completed.</summary>
/// <remarks>Source IDs in this exception identify logical Configlue source registrations, not physical resources.</remarks>
public sealed class StateMultiWriteException : Exception
{
    /// <summary>Creates a partial write failure with completed and pending source identities.</summary>
    public StateMultiWriteException(
        StateWriteReceipt completed,
        ResourceId? failedResourceId,
        IEnumerable<SourceId> failedSourceIds,
        IEnumerable<SourceId> unattemptedSourceIds,
        Exception innerException
    )
        : base("A multi-source write failed after partial completion.", innerException)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(failedSourceIds);
        ArgumentNullException.ThrowIfNull(unattemptedSourceIds);
        ArgumentNullException.ThrowIfNull(innerException);
        Completed = completed;
        FailedResourceId = failedResourceId;
        FailedSourceIds = Array.AsReadOnly(failedSourceIds.ToArray());
        UnattemptedSourceIds = Array.AsReadOnly(unattemptedSourceIds.ToArray());
    }

    /// <summary>The source results that completed before the failure.</summary>
    public StateWriteReceipt Completed { get; }

    /// <summary>The physical resource whose write failed, if it had an identity.</summary>
    public ResourceId? FailedResourceId { get; }

    /// <summary>The logical source registration IDs included in the failed physical write.</summary>
    public IReadOnlyList<SourceId> FailedSourceIds { get; }

    /// <summary>The logical source registration IDs whose writes were not attempted.</summary>
    public IReadOnlyList<SourceId> UnattemptedSourceIds { get; }
}
