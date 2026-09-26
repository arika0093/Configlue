namespace Configlue;

/// <summary>A logical source and its optional read, write, and watch capabilities.</summary>
public sealed class StateSource<T>
{
    /// <summary>Creates a source with at least a reader.</summary>
    public StateSource(
        string id,
        IStateReader<T> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        IStateWriter<T>? writer = null,
        IStateWatcher? watcher = null,
        string? physicalOrigin = null,
        ResourceId? resourceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(reader);
        if ((fallbackCondition & ~StateFallbackCondition.NotFoundOrUnavailable) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackCondition));
        }

        Id = id;
        Reader = reader;
        Priority = priority;
        FallbackCondition = fallbackCondition;
        Writer = writer;
        Watcher = watcher;
        PhysicalOrigin = physicalOrigin;
        ResourceId = resourceId ?? (reader as IResourceIdentity ?? writer as IResourceIdentity)?.ResourceId;
    }

    /// <summary>The stable logical identifier of the source.</summary>
    public string Id { get; }

    /// <summary>The source reader.</summary>
    public IStateReader<T> Reader { get; }

    /// <summary>The optional source writer.</summary>
    public IStateWriter<T>? Writer { get; }

    /// <summary>The optional source change watcher.</summary>
    public IStateWatcher? Watcher { get; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; }

    /// <summary>Read statuses that allow the next source to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; }

    /// <summary>The physical endpoint currently backing the logical source.</summary>
    public string? PhysicalOrigin { get; }

    /// <summary>The optional identity of the physical resource backing this logical source.</summary>
    public ResourceId? ResourceId { get; }
}
