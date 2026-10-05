using Configlue.Sources;

namespace Configlue;

/// <summary>Builds a state-source set from services registered in dependency injection.</summary>
/// <remarks>Low-level composition port: provider authors and advanced composition use this builder;
/// ordinary application code uses model-level registration helpers.</remarks>
/// <typeparam name="T">The generated sparse state fragment.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class StateSourceSetBuilder<T>
{
    private readonly List<Func<StateSource<T>>> _sourceFactories = [];
    private readonly HashSet<SourceId> _sourceIds = [];

    /// <summary>Adds a source with an automatically generated opaque identity.</summary>
    public StateSourceBuilder<T> Add(
        ISourceReader<T> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        string? physicalOrigin = null,
        ResourceId? fixedResourceId = null,
        string? logicalDescriptor = null
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        var source = new StateSource<T>(
            reader,
            new StateSourceOptions<T>
            {
                Priority = priority,
                FallbackCondition = fallbackCondition,
                Writer = (reader as ISourceCapabilities<T>)?.Writer ?? reader as ISourceWriter<T>,
                Watcher = (reader as ISourceCapabilities<T>)?.Watcher ?? reader as ISourceWatcher,
                PhysicalOrigin = physicalOrigin,
                FixedResourceId = fixedResourceId,
                LogicalDescriptor = logicalDescriptor,
            }
        );
        return Add(source.Id, reader, priority, fallbackCondition, physicalOrigin, fixedResourceId);
    }

    /// <summary>Adds a source and detects writer and watcher support on its reader.</summary>
    public StateSourceBuilder<T> Add(
        string id,
        ISourceReader<T> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        string? physicalOrigin = null,
        ResourceId? fixedResourceId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Add(
            SourceId.From(id),
            reader,
            priority,
            fallbackCondition,
            physicalOrigin,
            fixedResourceId
        );
    }

    /// <summary>Adds a source with a nominal logical identity.</summary>
    public StateSourceBuilder<T> Add(
        SourceId id,
        ISourceReader<T> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        string? physicalOrigin = null,
        ResourceId? fixedResourceId = null
    )
    {
        if (id.IsDefault)
        {
            throw new ArgumentException("A source ID must not be default.", nameof(id));
        }
        ArgumentNullException.ThrowIfNull(reader);
        ValidateFallbackCondition(fallbackCondition);
        AddId(id);

        var capabilities = reader as ISourceCapabilities<T>;
        var sourceBuilder = new StateSourceBuilder<T>(
            id,
            reader,
            priority,
            fallbackCondition,
            capabilities?.Writer ?? reader as ISourceWriter<T>,
            capabilities?.Watcher ?? reader as ISourceWatcher,
            physicalOrigin,
            fixedResourceId
        );
        _sourceFactories.Add(sourceBuilder.Build);
        return sourceBuilder;
    }

    /// <summary>Adds a fully configured source.</summary>
    public StateSourceSetBuilder<T> Add(StateSource<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        AddId(source.Id);
        _sourceFactories.Add(() => source);
        return this;
    }

    /// <summary>Builds an immutable source set in priority order.</summary>
    public StateSourceSet<T> Build() =>
        new(_sourceFactories.Select(static createSource => createSource()));

    private void AddId(SourceId id)
    {
        if (!_sourceIds.Add(id))
        {
            throw new ArgumentException(
                $"Source id '{id}' is registered more than once.",
                nameof(id)
            );
        }
    }

    private static void ValidateFallbackCondition(StateFallbackCondition fallbackCondition)
    {
        if ((fallbackCondition & ~StateFallbackCondition.NotFoundOrUnavailable) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackCondition));
        }
    }
}

/// <summary>Configures the optional capabilities of one state source.</summary>
/// <remarks>Low-level composition port for provider authors and advanced composition.</remarks>
/// <typeparam name="T">The generated sparse state fragment.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class StateSourceBuilder<T>
{
    private ISourceWriter<T>? _writer;
    private ISourceWatcher? _watcher;
    private readonly SourceId _id;
    private readonly ISourceReader<T> _reader;
    private readonly int _priority;
    private readonly StateFallbackCondition _fallbackCondition;
    private readonly string? _physicalOrigin;
    private readonly ResourceId? _fixedResourceId;
    private Func<IConfiglueSubject, ResourceKey> _resourceKeySelector = static subject =>
        ResourceKey.From(subject.Key);
    private Func<IConfiglueSubject, RouteKey> _routeSelector = static _ => RouteKey.Default;
    private RuntimeLifetimeRequirement _runtimeLifetime = RuntimeLifetimeRequirement.Shared;

    internal StateSourceBuilder(
        SourceId id,
        ISourceReader<T> reader,
        int priority,
        StateFallbackCondition fallbackCondition,
        ISourceWriter<T>? writer,
        ISourceWatcher? watcher,
        string? physicalOrigin,
        ResourceId? fixedResourceId
    )
    {
        _id = id;
        _reader = reader;
        _priority = priority;
        _fallbackCondition = fallbackCondition;
        _writer = writer;
        _watcher = watcher;
        _physicalOrigin = physicalOrigin;
        _fixedResourceId = fixedResourceId;
    }

    /// <summary>Sets or replaces the writer. The reader's writer is detected by default.</summary>
    public StateSourceBuilder<T> WithWriter(ISourceWriter<T> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        return this;
    }

    /// <summary>Sets or replaces the watcher. The reader's watcher is detected by default.</summary>
    public StateSourceBuilder<T> WithWatcher(ISourceWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        _watcher = watcher;
        return this;
    }

    /// <summary>Disables writing for this source, including an automatically detected writer.</summary>
    public StateSourceBuilder<T> WithoutWriter()
    {
        _writer = null;
        return this;
    }

    /// <summary>Disables watching for this source, including an automatically detected watcher.</summary>
    public StateSourceBuilder<T> WithoutWatcher()
    {
        _watcher = null;
        return this;
    }

    /// <summary>Maps this source's provider-facing resource key from a strongly typed application subject.</summary>
    public StateSourceBuilder<T> ResourceKeyBy<TSubject>(Func<TSubject, ResourceKey> selector)
        where TSubject : IConfiglueSubject
    {
        ArgumentNullException.ThrowIfNull(selector);
        _resourceKeySelector = subject =>
            subject is TSubject typed
                ? selector(typed)
                : throw new InvalidOperationException(
                    $"Source '{_id}' requires a subject of type '{typeof(TSubject)}', but received '{subject.GetType()}'."
                );
        return this;
    }

    /// <summary>Routes this source's physical placement from a strongly typed application subject.</summary>
    public StateSourceBuilder<T> RouteBy<TSubject>(Func<TSubject, RouteKey> selector)
        where TSubject : IConfiglueSubject
    {
        ArgumentNullException.ThrowIfNull(selector);
        _routeSelector = subject =>
            subject is TSubject typed
                ? selector(typed)
                : throw new InvalidOperationException(
                    $"Source '{_id}' requires a subject of type '{typeof(TSubject)}', but received '{subject.GetType()}'."
                );
        return this;
    }

    /// <summary>Declares the dependency-injection lifetime required by this source.</summary>
    public StateSourceBuilder<T> WithRuntimeLifetime(RuntimeLifetimeRequirement lifetime)
    {
        _runtimeLifetime = lifetime;
        return this;
    }

    internal StateSource<T> Build() =>
        new(
            _id,
            _reader,
            new StateSourceOptions<T>
            {
                Priority = _priority,
                FallbackCondition = _fallbackCondition,
                Writer = _writer,
                DisableWriteCapability = _writer is null,
                Watcher = _watcher,
                PhysicalOrigin = _physicalOrigin,
                FixedResourceId = _fixedResourceId,
                ResourceKeySelector = _resourceKeySelector,
                RuntimeLifetime = _runtimeLifetime,
                RouteSelector = _routeSelector,
            }
        );
}
