using Configlue.Resources;

namespace Configlue.Sources;

/// <summary>A logical source and its optional read, write, and watch capabilities.</summary>
public sealed class StateSource<T>
{
    private string[] _ownedPropertyPaths = [];
    private readonly Func<IConfiglueSubject, SubjectKey> _subjectKeySelector;
    private readonly ResourceId? _configuredResourceId;

    /// <summary>Creates a source with an automatically generated opaque logical identity.</summary>
    /// <remarks>
    /// When a resource identity or physical origin is available, the identity is stable for the same
    /// source descriptor. Supply <paramref name="logicalDescriptor"/> to distinguish multiple logical
    /// views over the same resource. Sources without a locator receive a registration-scoped identity.
    /// </remarks>
    public StateSource(
        ISourceReader<T> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        ISourceWriter<T>? writer = null,
        ISourceWatcher? watcher = null,
        string? physicalOrigin = null,
        ResourceId? resourceId = null,
        string? logicalDescriptor = null,
        bool explicitOnly = false,
        Func<IConfiglueSubject, SubjectKey>? subjectKeySelector = null,
        RuntimeLifetimeRequirement runtimeLifetime = RuntimeLifetimeRequirement.Shared,
        string? modelId = null
    )
        : this(
            StateSourceIdentity.Create(
                reader,
                physicalOrigin,
                resourceId ?? TryGetResourceId(reader, ConfiglueResourceContext.Default),
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
            subjectKeySelector,
            runtimeLifetime,
            modelId
        ) { }

    /// <summary>Creates a source from one object that supplies its read, write, and watch capabilities.</summary>
    public StateSource(
        ISourceCapabilities<T> source,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        string? physicalOrigin = null,
        ResourceId? resourceId = null,
        string? logicalDescriptor = null,
        bool explicitOnly = false,
        Func<IConfiglueSubject, SubjectKey>? subjectKeySelector = null,
        RuntimeLifetimeRequirement runtimeLifetime = RuntimeLifetimeRequirement.Shared,
        string? modelId = null
    )
        : this(
            (ISourceReader<T>)source,
            priority,
            fallbackCondition,
            source.Writer,
            source.Watcher,
            physicalOrigin,
            resourceId,
            logicalDescriptor,
            explicitOnly,
            subjectKeySelector,
            runtimeLifetime,
            modelId
        ) { }

    /// <summary>Creates a source from one capability-supplying object with an explicit logical identity.</summary>
    public StateSource(
        string id,
        ISourceCapabilities<T> source,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        string? physicalOrigin = null,
        ResourceId? resourceId = null,
        bool explicitOnly = false,
        Func<IConfiglueSubject, SubjectKey>? subjectKeySelector = null,
        RuntimeLifetimeRequirement runtimeLifetime = RuntimeLifetimeRequirement.Shared,
        string? modelId = null
    )
        : this(
            id,
            (ISourceReader<T>)source,
            priority,
            fallbackCondition,
            source.Writer,
            source.Watcher,
            physicalOrigin,
            resourceId,
            explicitOnly,
            subjectKeySelector,
            runtimeLifetime,
            modelId
        ) { }

    /// <summary>Creates a source with at least a reader.</summary>
    public StateSource(
        string id,
        ISourceReader<T> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        ISourceWriter<T>? writer = null,
        ISourceWatcher? watcher = null,
        string? physicalOrigin = null,
        ResourceId? resourceId = null,
        bool explicitOnly = false,
        Func<IConfiglueSubject, SubjectKey>? subjectKeySelector = null,
        RuntimeLifetimeRequirement runtimeLifetime = RuntimeLifetimeRequirement.Shared,
        string? modelId = null
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
            resourceId
            ?? TryGetResourceId(reader, ConfiglueResourceContext.Default)
            ?? TryGetResourceId(Writer, ConfiglueResourceContext.Default);
        _configuredResourceId = resourceId;
        ExplicitOnly = explicitOnly;
        RuntimeLifetime = runtimeLifetime;
        ModelId = modelId;
        _subjectKeySelector = subjectKeySelector ?? (static subject => subject.Key);
    }

    /// <summary>The stable logical identifier of the source.</summary>
    public string Id { get; }

    /// <summary>The source reader.</summary>
    public ISourceReader<T> Reader { get; }

    /// <summary>The optional source writer.</summary>
    public ISourceWriter<T>? Writer { get; }

    /// <summary>The optional source change watcher.</summary>
    public ISourceWatcher? Watcher { get; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; }

    /// <summary>Read statuses that allow the next source to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; }

    /// <summary>The physical endpoint currently backing the logical source.</summary>
    public string? PhysicalOrigin { get; }

    /// <summary>The optional identity of the physical resource backing this logical source.</summary>
    public ResourceId? ResourceId { get; }

    /// <summary>The stable Configlue model ID backing this logical source, when known.</summary>
    public string? ModelId { get; private set; }

    internal ResourceId? ConfiguredResourceId => _configuredResourceId;

    /// <summary>Whether this source is excluded from ordinary inferred write routing.</summary>
    public bool ExplicitOnly { get; private set; }

    /// <summary>The dependency-injection lifetime required by this source.</summary>
    public RuntimeLifetimeRequirement RuntimeLifetime { get; private set; }

    /// <summary>Resolves this logical source's key for an application-defined subject.</summary>
    public SubjectKey GetSubjectKey(IConfiglueSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return _subjectKeySelector(subject);
    }

    /// <summary>Resolves the physical resource identity for one application-defined subject.</summary>
    public ResourceId? GetResourceId(IConfiglueSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return GetResourceId(GetResourceContext(subject));
    }

    /// <summary>Resolves the physical resource identity for one resource operation context.</summary>
    public ResourceId? GetResourceId(ConfiglueResourceContext context)
    {
        if (_configuredResourceId is { } configured)
        {
            return configured;
        }

        return TryGetResourceId(Writer, context) ?? TryGetResourceId(Reader, context) ?? ResourceId;
    }

    /// <summary>Creates the resource context for a subject and this source's key mapping.</summary>
    public ConfiglueResourceContext GetResourceContext(
        IConfiglueSubject subject,
        RouteKey route = default
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        return new ConfiglueResourceContext(ModelId, subject, GetSubjectKey(subject), route);
    }

    /// <summary>Reads this source for a subject using its source-specific key mapping.</summary>
    public ValueTask<StateReadResult<T>> ReadAsync(
        IConfiglueSubject subject,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        return ReadAsync(GetResourceContext(subject), cancellationToken);
    }

    /// <summary>Reads this source using an already resolved resource context.</summary>
    public ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    ) => Reader.ReadAsync(context, cancellationToken);

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

        return Writer.WriteAsync(GetResourceContext(subject), request, cancellationToken);
    }

    /// <summary>Writes this source using an already resolved resource context.</summary>
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        if (Writer is null)
        {
            throw new InvalidOperationException($"State source '{Id}' does not support writes.");
        }

        return Writer.WriteAsync(context, request, cancellationToken);
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
            return new ValueTask(
                Task.FromException(
                    new InvalidOperationException($"State source '{Id}' does not support watching.")
                )
            );
        }

        return WaitForChangeAsync(GetResourceContext(subject), observedRevision, cancellationToken);
    }

    /// <summary>Watches this source using an already resolved resource context.</summary>
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) =>
        Watcher is null
            ? new ValueTask(
                Task.FromException(
                    new InvalidOperationException($"State source '{Id}' does not support watching.")
                )
            )
            : Watcher.WaitForChangeAsync(context, observedRevision, cancellationToken);

    internal IReadOnlyList<string> OwnedPropertyPaths => _ownedPropertyPaths;

    private static ResourceId? TryGetResourceId(
        object? resource,
        ConfiglueResourceContext context
    ) =>
        resource is IResourceIdentity identity && identity.TryGetResourceId(context, out var id)
            ? id
            : null;

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
            _configuredResourceId,
            ExplicitOnly,
            _subjectKeySelector,
            RuntimeLifetime,
            ModelId
        )
        {
            _ownedPropertyPaths = ownedPaths,
        };
        return clone;
    }

    internal StateSource<T> WithModelId(string? modelId)
    {
        if (string.Equals(ModelId, modelId, StringComparison.Ordinal))
        {
            return this;
        }

        return new StateSource<T>(
            Id,
            Reader,
            Priority,
            FallbackCondition,
            Writer,
            Watcher,
            PhysicalOrigin,
            _configuredResourceId,
            ExplicitOnly,
            _subjectKeySelector,
            RuntimeLifetime,
            modelId
        )
        {
            _ownedPropertyPaths = [.. _ownedPropertyPaths],
        };
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
            _configuredResourceId,
            ExplicitOnly,
            subjectKeySelector,
            RuntimeLifetime,
            ModelId
        );
    }

    internal void CopyRoutingMetadataTo<TTarget>(
        StateSource<TTarget> target,
        bool? explicitOnly = null
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        target.ExplicitOnly = explicitOnly ?? ExplicitOnly;
        target.RuntimeLifetime = RuntimeLifetime;
        target.ModelId = ModelId;
        target._ownedPropertyPaths = [.. _ownedPropertyPaths];
    }
}
