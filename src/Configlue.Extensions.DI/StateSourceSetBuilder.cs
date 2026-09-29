namespace Configlue;

/// <summary>Builds a state-source set from services registered in dependency injection.</summary>
/// <typeparam name="T">The generated sparse state fragment.</typeparam>
public sealed class StateSourceSetBuilder<T>
{
    private readonly List<Func<StateSource<T>>> _sourceFactories = [];
    private readonly HashSet<string> _sourceIds = new(StringComparer.Ordinal);

    /// <summary>Adds a source with an automatically generated opaque identity.</summary>
    public StateSourceBuilder<T> Add(
        IStateReader<T> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        string? physicalOrigin = null,
        ResourceId? resourceId = null,
        string? logicalDescriptor = null
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        var source = new StateSource<T>(
            reader,
            priority,
            fallbackCondition,
            reader as IStateWriter<T>,
            reader as IStateWatcher,
            physicalOrigin,
            resourceId,
            logicalDescriptor
        );
        return Add(source.Id, reader, priority, fallbackCondition, physicalOrigin, resourceId);
    }

    /// <summary>Adds a source and detects writer and watcher support on its reader.</summary>
    public StateSourceBuilder<T> Add(
        string id,
        IStateReader<T> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        string? physicalOrigin = null,
        ResourceId? resourceId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(reader);
        ValidateFallbackCondition(fallbackCondition);
        AddId(id);

        var sourceBuilder = new StateSourceBuilder<T>(
            id,
            reader,
            priority,
            fallbackCondition,
            reader as IStateWriter<T>,
            reader as IStateWatcher,
            physicalOrigin,
            resourceId
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

    private void AddId(string id)
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
        if (
            (
                fallbackCondition
                & ~(StateFallbackCondition.NotFoundOrUnavailable | StateFallbackCondition.Invalid)
            ) != 0
        )
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackCondition));
        }
    }
}

/// <summary>Configures the optional capabilities of one state source.</summary>
/// <typeparam name="T">The generated sparse state fragment.</typeparam>
public sealed class StateSourceBuilder<T>
{
    private IStateWriter<T>? _writer;
    private IStateWatcher? _watcher;
    private readonly string _id;
    private readonly IStateReader<T> _reader;
    private readonly int _priority;
    private readonly StateFallbackCondition _fallbackCondition;
    private readonly string? _physicalOrigin;
    private readonly ResourceId? _resourceId;
    private Func<IConfiglueSubject, SubjectKey> _subjectKeySelector = static subject => subject.Key;

    internal StateSourceBuilder(
        string id,
        IStateReader<T> reader,
        int priority,
        StateFallbackCondition fallbackCondition,
        IStateWriter<T>? writer,
        IStateWatcher? watcher,
        string? physicalOrigin,
        ResourceId? resourceId
    )
    {
        _id = id;
        _reader = reader;
        _priority = priority;
        _fallbackCondition = fallbackCondition;
        _writer = writer;
        _watcher = watcher;
        _physicalOrigin = physicalOrigin;
        _resourceId = resourceId;
    }

    /// <summary>Sets or replaces the writer. The reader's writer is detected by default.</summary>
    public StateSourceBuilder<T> WithWriter(IStateWriter<T> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        return this;
    }

    /// <summary>Sets or replaces the watcher. The reader's watcher is detected by default.</summary>
    public StateSourceBuilder<T> WithWatcher(IStateWatcher watcher)
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

    /// <summary>Maps this source's key from a strongly typed application subject.</summary>
    public StateSourceBuilder<T> KeyBy<TSubject>(Func<TSubject, SubjectKey> selector)
        where TSubject : IConfiglueSubject
    {
        ArgumentNullException.ThrowIfNull(selector);
        _subjectKeySelector = subject =>
            subject is TSubject typed
                ? selector(typed)
                : throw new InvalidOperationException(
                    $"Source '{_id}' requires a subject of type '{typeof(TSubject)}', but received '{subject.GetType()}'."
                );
        return this;
    }

    internal StateSource<T> Build() =>
        new(
            _id,
            _reader,
            _priority,
            _fallbackCondition,
            _writer,
            _watcher,
            _physicalOrigin,
            _resourceId,
            subjectKeySelector: _subjectKeySelector
        );
}
