using Configlue.Resources;

namespace Configlue.Sources;

/// <summary>A logical source and its optional read, write, and watch capabilities.</summary>
public sealed class StateSource<T>
{
    private string[] _ownedPropertyPaths = [];
    private readonly Func<IConfiglueSubject, ResourceKey> _resourceKeySelector;
    private Func<IConfiglueSubject, RouteKey> RouteSelector { get; set; }
    private readonly ResourceId? _fixedResourceId;

    /// <summary>Creates a source with an automatically generated opaque logical identity.</summary>
    /// <remarks>
    /// Supply <paramref name="logicalDescriptor"/> to distinguish multiple logical registrations of
    /// the same reader. Without a descriptor this source receives a registration-scoped identifier.
    /// Physical origins and resolved resource identities are not logical source identifiers.
    /// </remarks>
    public StateSource(
        ISourceReader<T> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        ISourceWriter<T>? writer = null,
        ISourceWatcher? watcher = null,
        string? physicalOrigin = null,
        ResourceId? fixedResourceId = null,
        string? logicalDescriptor = null,
        bool explicitOnly = false,
        Func<IConfiglueSubject, ResourceKey>? resourceKeySelector = null,
        RuntimeLifetimeRequirement runtimeLifetime = RuntimeLifetimeRequirement.Shared,
        string? modelId = null,
        Func<IConfiglueSubject, RouteKey>? routeSelector = null
    )
        : this(
            StateSourceIdentity.Create(reader, logicalDescriptor),
            reader,
            priority,
            fallbackCondition,
            writer,
            watcher,
            physicalOrigin,
            fixedResourceId,
            explicitOnly,
            resourceKeySelector,
            runtimeLifetime,
            modelId,
            routeSelector
        ) { }

    /// <summary>Creates a source from one object that supplies its read, write, and watch capabilities.</summary>
    public StateSource(
        ISourceCapabilities<T> source,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        string? physicalOrigin = null,
        ResourceId? fixedResourceId = null,
        string? logicalDescriptor = null,
        bool explicitOnly = false,
        Func<IConfiglueSubject, ResourceKey>? resourceKeySelector = null,
        RuntimeLifetimeRequirement runtimeLifetime = RuntimeLifetimeRequirement.Shared,
        string? modelId = null,
        Func<IConfiglueSubject, RouteKey>? routeSelector = null
    )
        : this(
            (ISourceReader<T>)source,
            priority,
            fallbackCondition,
            source.Writer,
            source.Watcher,
            physicalOrigin,
            fixedResourceId,
            logicalDescriptor,
            explicitOnly,
            resourceKeySelector,
            runtimeLifetime,
            modelId,
            routeSelector
        ) { }

    /// <summary>Creates a source from one capability-supplying object with an explicit logical identity.</summary>
    public StateSource(
        string id,
        ISourceCapabilities<T> source,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        string? physicalOrigin = null,
        ResourceId? fixedResourceId = null,
        bool explicitOnly = false,
        Func<IConfiglueSubject, ResourceKey>? resourceKeySelector = null,
        RuntimeLifetimeRequirement runtimeLifetime = RuntimeLifetimeRequirement.Shared,
        string? modelId = null,
        Func<IConfiglueSubject, RouteKey>? routeSelector = null
    )
        : this(
            id,
            (ISourceReader<T>)source,
            priority,
            fallbackCondition,
            source.Writer,
            source.Watcher,
            physicalOrigin,
            fixedResourceId,
            explicitOnly,
            resourceKeySelector,
            runtimeLifetime,
            modelId,
            routeSelector
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
        ResourceId? fixedResourceId = null,
        bool explicitOnly = false,
        Func<IConfiglueSubject, ResourceKey>? resourceKeySelector = null,
        RuntimeLifetimeRequirement runtimeLifetime = RuntimeLifetimeRequirement.Shared,
        string? modelId = null,
        Func<IConfiglueSubject, RouteKey>? routeSelector = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(reader);
        if (
            (
                fallbackCondition
                & ~(
                    StateFallbackCondition.NotFoundOrUnavailable
                    | StateFallbackCondition.InvalidPayload
                )
            ) != 0
        )
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackCondition));
        }
        if (fixedResourceId is { IsDefault: true })
        {
            throw new ArgumentException(
                "A configured resource identity must not be the default ResourceId.",
                nameof(fixedResourceId)
            );
        }

        Id = id;
        Reader = reader;
        Priority = priority;
        FallbackCondition = fallbackCondition;
        Writer = writer;
        Watcher = watcher;
        PhysicalOrigin = physicalOrigin;
        _fixedResourceId = fixedResourceId;
        ExplicitOnly = explicitOnly;
        RuntimeLifetime = runtimeLifetime;
        ModelId = modelId;
        _resourceKeySelector =
            resourceKeySelector ?? (static subject => ResourceKey.From(subject.Key));
        RouteSelector = routeSelector ?? (static _ => RouteKey.Default);
    }

    /// <summary>The identifier of this logical source registration, independent of physical resource identity.</summary>
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

    /// <summary>Human-readable physical location metadata for diagnostics; it is not a resource identity contract.</summary>
    public string? PhysicalOrigin { get; }

    /// <summary>The explicit physical identity override shared by all operation contexts, when configured.</summary>
    public ResourceId? FixedResourceId => _fixedResourceId;

    /// <summary>The stable Configlue model ID backing this logical source, when known.</summary>
    public string? ModelId { get; private set; }

    internal ResourceId? ConfiguredResourceId => _fixedResourceId;

    /// <summary>Whether this source is excluded from ordinary inferred write routing.</summary>
    public bool ExplicitOnly { get; private set; }

    /// <summary>The dependency-injection lifetime required by this source.</summary>
    public RuntimeLifetimeRequirement RuntimeLifetime { get; private set; }

    /// <summary>Resolves this logical source's key for an application-defined subject.</summary>
    public ResourceKey GetResourceKey(IConfiglueSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return _resourceKeySelector(subject);
    }

    /// <summary>Resolves this logical source's physical route for an application-defined subject.</summary>
    public RouteKey GetRouteKey(IConfiglueSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return RouteSelector(subject);
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
context = ConfiglueResourceContext.Normalize(context);
        if (_fixedResourceId is { } configured)
        {
            return configured;
        }

        return TryGetResourceId(Writer, context) ?? TryGetResourceId(Reader, context);
    }

    /// <summary>Creates the complete resource context for a subject using this source's key and route mapping.</summary>
    public ConfiglueResourceContext GetResourceContext(IConfiglueSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return new ConfiglueResourceContext(
            ModelId,
            subject,
            GetResourceKey(subject),
            GetRouteKey(subject)
        );
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
    ) => Reader.ReadAsync(ConfiglueResourceContext.Normalize(context), cancellationToken);

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

        return Writer.WriteAsync(
            ConfiglueResourceContext.Normalize(context),
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
            : Watcher.WaitForChangeAsync(
                ConfiglueResourceContext.Normalize(context),
                observedRevision,
                cancellationToken
            );

    internal IReadOnlyList<string> OwnedPropertyPaths => _ownedPropertyPaths;

    private static ResourceId? TryGetResourceId(
        object? resource,
        ConfiglueResourceContext context
    ) => resource.TryGetResourceId(context, out var resourceId) ? resourceId : null;

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
            _fixedResourceId,
            ExplicitOnly,
            _resourceKeySelector,
            RuntimeLifetime,
            ModelId,
            RouteSelector
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
            _fixedResourceId,
            ExplicitOnly,
            _resourceKeySelector,
            RuntimeLifetime,
            modelId,
            RouteSelector
        )
        {
            _ownedPropertyPaths = [.. _ownedPropertyPaths],
        };
    }

    internal StateSource<T> WithResourceKeySelector(
        Func<IConfiglueSubject, ResourceKey> resourceKeySelector
    )
    {
        ArgumentNullException.ThrowIfNull(resourceKeySelector);
        return new StateSource<T>(
            Id,
            Reader,
            Priority,
            FallbackCondition,
            Writer,
            Watcher,
            PhysicalOrigin,
            _fixedResourceId,
            ExplicitOnly,
            resourceKeySelector,
            RuntimeLifetime,
            ModelId,
            RouteSelector
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
        target.RouteSelector = RouteSelector;
        target._ownedPropertyPaths = [.. _ownedPropertyPaths];
    }
}
