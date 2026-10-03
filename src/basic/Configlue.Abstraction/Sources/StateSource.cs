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
    public StateSource(ISourceReader<T> reader, StateSourceOptions<T>? options = null)
        : this(
            SourceId.From(StateSourceIdentity.Create(reader, options?.LogicalDescriptor)),
            reader,
            options ?? new StateSourceOptions<T>()
        ) { }

    /// <summary>Creates a source with an explicit logical identity.</summary>
    public StateSource(string id, ISourceReader<T> reader, StateSourceOptions<T> options)
        : this(SourceId.From(id), reader, options) { }

    /// <summary>Creates a source with an explicit logical identity.</summary>
    public StateSource(SourceId id, ISourceReader<T> reader, StateSourceOptions<T> options)
    {
        if (id.IsDefault)
        {
            throw new ArgumentException(
                "A source identifier must not be the default SourceId.",
                nameof(id)
            );
        }
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        if (
            (
                options.FallbackCondition
                & ~(
                    StateFallbackCondition.NotFoundOrUnavailable
                    | StateFallbackCondition.InvalidPayload
                )
            ) != 0
        )
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (options.FixedResourceId is { IsDefault: true })
        {
            throw new ArgumentException(
                "A configured resource identity must not be the default ResourceId.",
                nameof(options)
            );
        }

        var capabilities = reader as ISourceCapabilities<T>;
        Id = id;
        Reader = reader;
        Priority = options.Priority;
        FallbackCondition = options.FallbackCondition;
        Writer = options.DisableWriteCapability ? null : options.Writer ?? capabilities?.Writer;
        Watcher = options.Watcher ?? capabilities?.Watcher;
        PhysicalOrigin = options.PhysicalOrigin;
        _fixedResourceId = options.FixedResourceId;
        ExplicitOnly = options.ExplicitOnly;
        RuntimeLifetime = options.RuntimeLifetime;
        ModelId = options.ModelId;
        _resourceKeySelector =
            options.ResourceKeySelector ?? (static subject => ResourceKey.From(subject.Key));
        RouteSelector = options.RouteSelector ?? (static _ => RouteKey.Default);
    }

    // Kept internal for in-assembly construction paths while callers move to StateSourceOptions<T>.
    internal StateSource(
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
            reader,
            new StateSourceOptions<T>
            {
                Priority = priority,
                FallbackCondition = fallbackCondition,
                Writer = writer,
                Watcher = watcher,
                PhysicalOrigin = physicalOrigin,
                FixedResourceId = fixedResourceId,
                LogicalDescriptor = logicalDescriptor,
                ExplicitOnly = explicitOnly,
                ResourceKeySelector = resourceKeySelector,
                RuntimeLifetime = runtimeLifetime,
                ModelId = modelId,
                RouteSelector = routeSelector,
            }
        ) { }

    internal StateSource(
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
        : this(
            SourceId.From(id),
            reader,
            new StateSourceOptions<T>
            {
                Priority = priority,
                FallbackCondition = fallbackCondition,
                Writer = writer,
                Watcher = watcher,
                PhysicalOrigin = physicalOrigin,
                FixedResourceId = fixedResourceId,
                ExplicitOnly = explicitOnly,
                ResourceKeySelector = resourceKeySelector,
                RuntimeLifetime = runtimeLifetime,
                ModelId = modelId,
                RouteSelector = routeSelector,
            }
        ) { }

    internal StateSource(
        SourceId id,
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
        : this(
            id,
            reader,
            new StateSourceOptions<T>
            {
                Priority = priority,
                FallbackCondition = fallbackCondition,
                Writer = writer,
                Watcher = watcher,
                PhysicalOrigin = physicalOrigin,
                FixedResourceId = fixedResourceId,
                ExplicitOnly = explicitOnly,
                ResourceKeySelector = resourceKeySelector,
                RuntimeLifetime = runtimeLifetime,
                ModelId = modelId,
                RouteSelector = routeSelector,
            }
        ) { }

    /// <summary>The identifier of this logical source registration, independent of physical resource identity.</summary>
    public SourceId Id { get; }

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
        ArgumentNullException.ThrowIfNull(request);
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
        ArgumentNullException.ThrowIfNull(request);
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
        var clone = new StateSource<T>(Id, Reader, CreateOptions())
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

        return new StateSource<T>(Id, Reader, CreateOptions(modelId: modelId, replaceModelId: true))
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
            CreateOptions(resourceKeySelector: resourceKeySelector)
        );
    }

    private StateSourceOptions<T> CreateOptions(
        string? modelId = null,
        Func<IConfiglueSubject, ResourceKey>? resourceKeySelector = null,
        bool replaceModelId = false
    ) =>
        new()
        {
            Priority = Priority,
            FallbackCondition = FallbackCondition,
            Writer = Writer,
            DisableWriteCapability = Writer is null,
            Watcher = Watcher,
            PhysicalOrigin = PhysicalOrigin,
            FixedResourceId = _fixedResourceId,
            ExplicitOnly = ExplicitOnly,
            ResourceKeySelector = resourceKeySelector ?? _resourceKeySelector,
            RuntimeLifetime = RuntimeLifetime,
            ModelId = replaceModelId ? modelId : ModelId,
            RouteSelector = RouteSelector,
        };

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
