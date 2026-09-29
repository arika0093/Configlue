using Configlue.Resources;

namespace Configlue.Sources;

/// <summary>A logical source and its optional read, write, and watch capabilities.</summary>
public sealed class StateSource<T>
{
    private string[] _ownedPropertyPaths = [];
    private readonly Func<IConfiglueSubject, SubjectKey> _subjectKeySelector;

    /// <summary>Creates a source with an automatically generated opaque logical identity.</summary>
    /// <remarks>
    /// When a resource identity or physical origin is available, the identity is stable for the same
    /// source descriptor. Supply <paramref name="logicalDescriptor"/> to distinguish multiple logical
    /// views over the same resource. Sources without a locator receive a registration-scoped identity.
    /// </remarks>
    public StateSource(
        IStateReader<T> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        IStateWriter<T>? writer = null,
        IStateWatcher? watcher = null,
        string? physicalOrigin = null,
        ResourceId? resourceId = null,
        string? logicalDescriptor = null,
        bool explicitOnly = false,
        Func<IConfiglueSubject, SubjectKey>? subjectKeySelector = null
    )
        : this(
            StateSourceIdentity.Create(
                reader,
                physicalOrigin,
                resourceId ?? (reader as IResourceIdentity)?.ResourceId,
                logicalDescriptor
            ),
            reader,
            priority,
            fallbackCondition,
            writer,
            watcher,
            physicalOrigin,
            resourceId,
            explicitOnly,
            subjectKeySelector
        ) { }

    /// <summary>Creates a source with at least a reader.</summary>
    public StateSource(
        string id,
        IStateReader<T> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        IStateWriter<T>? writer = null,
        IStateWatcher? watcher = null,
        string? physicalOrigin = null,
        ResourceId? resourceId = null,
        bool explicitOnly = false,
        Func<IConfiglueSubject, SubjectKey>? subjectKeySelector = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(reader);
        if (
            (
                fallbackCondition
                & ~(StateFallbackCondition.NotFoundOrUnavailable | StateFallbackCondition.Invalid)
            ) != 0
        )
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
        ResourceId =
            resourceId ?? (reader as IResourceIdentity ?? writer as IResourceIdentity)?.ResourceId;
        ExplicitOnly = explicitOnly;
        _subjectKeySelector = subjectKeySelector ?? (static subject => subject.Key);
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

    /// <summary>Whether this source is excluded from ordinary inferred write routing.</summary>
    public bool ExplicitOnly { get; private set; }

    /// <summary>Resolves this logical source's key for an application-defined subject.</summary>
    public SubjectKey GetSubjectKey(IConfiglueSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return _subjectKeySelector(subject);
    }

    /// <summary>Reads this source for a subject using its source-specific key mapping.</summary>
    public ValueTask<StateReadResult<T>> ReadAsync(
        IConfiglueSubject subject,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Reader.ReadAsync(
            new ConfiglueResourceContext(subject, GetSubjectKey(subject)),
            cancellationToken
        );
    }

    /// <summary>Writes this source for a subject using its source-specific key mapping.</summary>
    public ValueTask<StateWriteResult> WriteAsync(
        IConfiglueSubject subject,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (Writer is null)
        {
            throw new InvalidOperationException($"State source '{Id}' does not support writes.");
        }

        return Writer.WriteAsync(
            new ConfiglueResourceContext(subject, GetSubjectKey(subject)),
            request,
            cancellationToken
        );
    }

    /// <summary>Watches this source for one subject using its source-specific key mapping.</summary>
    public ValueTask WaitForChangeAsync(
        IConfiglueSubject subject,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (Watcher is null)
        {
            return ValueTask.FromException(
                new InvalidOperationException($"State source '{Id}' does not support watching.")
            );
        }

        return Watcher.WaitForChangeAsync(
            new ConfiglueResourceContext(subject, GetSubjectKey(subject)),
            observedRevision,
            cancellationToken
        );
    }

    internal IReadOnlyList<string> OwnedPropertyPaths => _ownedPropertyPaths;

    internal StateSource<T> WithWriteOwnership(string propertyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        string[] ownedPaths =
            _ownedPropertyPaths.Length == 0
                ? [propertyPath]
                : _ownedPropertyPaths.Select(path => $"{propertyPath}.{path}").ToArray();
        var clone = new StateSource<T>(
            Id,
            Reader,
            Priority,
            FallbackCondition,
            Writer,
            Watcher,
            PhysicalOrigin,
            ResourceId,
            ExplicitOnly,
            _subjectKeySelector
        )
        {
            _ownedPropertyPaths = ownedPaths,
        };
        return clone;
    }

    internal StateSource<T> WithSubjectKeySelector(
        Func<IConfiglueSubject, SubjectKey> subjectKeySelector
    )
    {
        ArgumentNullException.ThrowIfNull(subjectKeySelector);
        return new StateSource<T>(
            Id,
            Reader,
            Priority,
            FallbackCondition,
            Writer,
            Watcher,
            PhysicalOrigin,
            ResourceId,
            ExplicitOnly,
            subjectKeySelector
        );
    }

    internal void CopyRoutingMetadataTo<TTarget>(
        StateSource<TTarget> target,
        bool? explicitOnly = null
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        target.ExplicitOnly = explicitOnly ?? ExplicitOnly;
        target._ownedPropertyPaths = [.. _ownedPropertyPaths];
    }
}
