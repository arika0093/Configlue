using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Configlue;

/// <summary>A thread-safe registry that creates and owns named Configlue states.</summary>
/// <typeparam name="TModel">The configuration model.</typeparam>
/// <remarks>
/// <para>
/// Registry mutation and disposal are authoritative: <c>TryAddAsync</c>,
/// <c>TryRemoveAsync</c>, <c>ClearAsync</c> and <c>DisposeAsync</c> update the registry
/// first, dispose the retired runtimes and their owned resources, and then raise
/// <c>StateAdded</c>/<c>StateRemoved</c> for their own transitions.
/// </para>
/// <para>
/// Notifications are dispatched synchronously on the mutating caller's thread without
/// holding the registry lock, and are serialized so handlers never run concurrently.
/// There is no global ordering across concurrent operations: each operation delivers
/// only its own transitions before it completes and never waits for another operation's
/// handlers. While notifications are deferred through
/// <see cref="IConfiglueStateRegistryNotificationDeferrer{TModel}"/>, events are queued in
/// first-in first-out order and delivered when the last deferral scope is disposed.
/// </para>
/// <para>
/// A listener exception is logged and does not prevent other listeners from receiving the
/// notification. Listeners must be quick and must not synchronously wait for registry
/// operations: a handler that blocks delays the operation that raised it, and waiting for
/// registry disposal from inside a handler deadlocks.
/// </para>
/// <para>
/// Compatibility: <c>ClearAsync</c>/<c>DisposeAsync</c> neither wait for concurrent
/// in-flight removals nor aggregate their errors, and an empty <c>ClearAsync</c> never
/// throws for another operation's failure. While deferred, mutation/disposal completion
/// precedes notification delivery (a deferred <c>DisposeAsync</c> completes once entries
/// are retired and disposed). <c>StateNames</c> throws <c>ObjectDisposedException</c> once
/// disposal has started.
/// </para>
/// </remarks>
internal sealed class ConfiglueOwnedStateRegistry<TModel>
    : IConfiglueStateRegistry<TModel>,
        IConfiglueStateRegistryNotificationDeferrer<TModel>
{
    private sealed class Entry(IWritableState<TModel> runtime, object[] resources)
    {
        public IWritableState<TModel> Runtime { get; } = runtime;
        public object[] Resources { get; } = resources;
    }

    private sealed class DeferredNotification(IWritableState<TModel> runtime, Action dispatch)
    {
        public IWritableState<TModel> Runtime { get; } = runtime;
        public Action Dispatch { get; } = dispatch;
    }

    private readonly Func<string, (IWritableState<TModel> Runtime, object[] Resources)> _factory;
    private readonly HashSet<string> _reservedNames;
    private readonly object _gate = new();
    private readonly object _notificationGate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retiringNames = new(StringComparer.Ordinal);
    private readonly Queue<DeferredNotification> _deferred = new();
    private int _deferralCount;
    private Task? _disposeTask;
    private bool _disposed;

    /// <summary>Creates a registry using a factory that builds a state and its owned resources from its name.</summary>
    public ConfiglueOwnedStateRegistry(
        Func<string, (IWritableState<TModel> Runtime, object[] Resources)> factory,
        IEnumerable<string> reservedNames
    )
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(reservedNames);
        _factory = factory;
        _reservedNames = new HashSet<string>(reservedNames, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public event Action<string, IWritableState<TModel>>? StateAdded;

    /// <inheritdoc />
    public event Action<string>? StateRemoved;

    /// <inheritdoc />
    public IReadOnlyCollection<string> StateNames
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return Array.AsReadOnly(_entries.Keys.ToArray());
            }
        }
    }

    /// <inheritdoc />
    public IWritableState<TModel> Get(string stateName)
    {
        ValidateName(stateName);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _entries.TryGetValue(stateName, out var entry)
                ? entry.Runtime
                : throw new KeyNotFoundException(
                    $"Configlue state '{stateName}' is not registered dynamically."
                );
        }
    }

    /// <inheritdoc />
    public bool TryGet(string stateName, out IWritableState<TModel>? state)
    {
        ValidateName(stateName);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(stateName, out var entry))
            {
                state = entry.Runtime;
                return true;
            }

            state = null;
            return false;
        }
    }

    internal IReadOnlyList<object> GetOwnedResourcesForTests(string stateName)
    {
        ValidateName(stateName);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _entries.TryGetValue(stateName, out var entry)
                ? Array.AsReadOnly(entry.Resources.ToArray())
                : throw new KeyNotFoundException(
                    $"Configlue state '{stateName}' is not registered dynamically."
                );
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TryAddAsync(string stateName)
    {
        ValidateName(stateName);
        Entry entry;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (
                _reservedNames.Contains(stateName)
                || _entries.ContainsKey(stateName)
                || _retiringNames.Contains(stateName)
            )
            {
                return new ValueTask<bool>(false);
            }

            var created = _factory(stateName);
            entry = new Entry(
                created.Runtime
                    ?? throw new InvalidOperationException("The state factory returned null."),
                created.Resources ?? []
            );
            _entries.Add(stateName, entry);
        }

        EnqueueOrDispatch(entry.Runtime, () => NotifyAdded(stateName, entry.Runtime));
        return new ValueTask<bool>(true);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryRemoveAsync(string stateName)
    {
        ValidateName(stateName);
        Entry entry;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(stateName, out var registered))
            {
                return false;
            }

            _entries.Remove(stateName);
            _retiringNames.Add(stateName);
            entry = registered;
        }

        Exception? disposalError = null;
        try
        {
            await DisposeEntryAsync(entry).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposalError = exception;
        }

        lock (_gate)
        {
            _retiringNames.Remove(stateName);
        }

        EnqueueOrDispatch(entry.Runtime, () => NotifyRemoved(stateName));

        if (disposalError is not null)
        {
            ExceptionDispatchInfo.Capture(disposalError).Throw();
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask ClearAsync()
    {
        KeyValuePair<string, Entry>[] removed;
        lock (_gate)
        {
            ThrowIfDisposed();
            removed = _entries.ToArray();
            _entries.Clear();
            foreach (var (name, _) in removed)
            {
                _retiringNames.Add(name);
            }
        }

        var disposalErrors = new List<Exception>();
        foreach (var (_, entry) in removed)
        {
            try
            {
                await DisposeEntryAsync(entry).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddCleanupError(disposalErrors, exception);
            }
        }

        lock (_gate)
        {
            foreach (var (name, _) in removed)
            {
                _retiringNames.Remove(name);
            }
        }

        foreach (var (name, entry) in removed)
        {
            EnqueueOrDispatch(entry.Runtime, () => NotifyRemoved(name));
        }

        if (disposalErrors.Count > 0)
        {
            throw new AggregateException(
                "One or more Configlue states failed to clear.",
                disposalErrors
            );
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        KeyValuePair<string, Entry>[] removed;
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            removed = _entries.ToArray();
            _entries.Clear();
            foreach (var (name, _) in removed)
            {
                _retiringNames.Add(name);
            }

            _disposeTask = disposeTask = FinishDisposeAsync(removed);
        }

        return new ValueTask(disposeTask);
    }

    private async Task FinishDisposeAsync(KeyValuePair<string, Entry>[] removed)
    {
        var disposalErrors = new List<Exception>();
        foreach (var (_, entry) in removed)
        {
            try
            {
                await DisposeEntryAsync(entry).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddCleanupError(disposalErrors, exception);
            }
        }

        lock (_gate)
        {
            foreach (var (name, _) in removed)
            {
                _retiringNames.Remove(name);
            }
        }

        foreach (var (name, entry) in removed)
        {
            EnqueueOrDispatch(entry.Runtime, () => NotifyRemoved(name));
        }

        if (disposalErrors.Count > 0)
        {
            throw new AggregateException(
                "One or more Configlue states failed to dispose.",
                disposalErrors
            );
        }
    }

    private static async ValueTask DisposeEntryAsync(Entry entry)
    {
        List<Exception>? errors = null;
        try
        {
            await ConfiglueOwnedResources.DisposeAsync(entry.Runtime).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddCleanupError(ref errors, exception);
        }

        foreach (var resource in entry.Resources)
        {
            try
            {
                await ConfiglueOwnedResources.DisposeAsync(resource).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddCleanupError(ref errors, exception);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException(
                "A dynamic Configlue state runtime failed to dispose.",
                errors
            );
        }
    }

    IConfiglueStateRegistryNotificationDeferral<TModel> IConfiglueStateRegistryNotificationDeferrer<TModel>.DeferNotifications()
    {
        lock (_gate)
        {
            _deferralCount++;
        }

        return new NotificationScope(this);
    }

    private void CancelNotifications(IWritableState<TModel> runtime)
    {
        lock (_gate)
        {
            if (_deferred.Count == 0)
            {
                return;
            }

            var retained = _deferred
                .Where(notification => !ReferenceEquals(notification.Runtime, runtime))
                .ToArray();
            _deferred.Clear();
            foreach (var notification in retained)
            {
                _deferred.Enqueue(notification);
            }
        }
    }

    private void ReleaseNotificationDeferral()
    {
        DeferredNotification[] pending;
        lock (_gate)
        {
            if (_deferralCount <= 0)
            {
                throw new InvalidOperationException("No notification deferral is active.");
            }

            _deferralCount--;
            if (_deferralCount > 0 || _deferred.Count == 0)
            {
                return;
            }

            pending = _deferred.ToArray();
            _deferred.Clear();
        }

        foreach (var notification in pending)
        {
            Dispatch(notification.Dispatch);
        }
    }

    private sealed class NotificationScope(ConfiglueOwnedStateRegistry<TModel> owner)
        : IConfiglueStateRegistryNotificationDeferral<TModel>
    {
        private ConfiglueOwnedStateRegistry<TModel>? _owner = owner;

        public void Cancel(IWritableState<TModel> runtime)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            var current = Volatile.Read(ref _owner);
            if (current is null)
            {
                throw new ObjectDisposedException(nameof(NotificationScope));
            }

            current.CancelNotifications(runtime);
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseNotificationDeferral();
    }

    private void EnqueueOrDispatch(IWritableState<TModel> runtime, Action dispatch)
    {
        var deferred = false;
        lock (_gate)
        {
            if (_deferralCount > 0)
            {
                _deferred.Enqueue(new DeferredNotification(runtime, dispatch));
                deferred = true;
            }
        }

        if (!deferred)
        {
            Dispatch(dispatch);
        }
    }

    private void Dispatch(Action dispatch)
    {
        // Notifications are serialized but the registry lock is never held here, so reentrant
        // registry calls cannot deadlock on the lock itself. Handlers must still avoid
        // synchronously waiting for registry operations.
        lock (_notificationGate)
        {
            dispatch();
        }
    }

    private void NotifyAdded(string name, IWritableState<TModel> state)
    {
        if (StateAdded is not { } handlers)
        {
            return;
        }

        foreach (
            var handler in handlers
                .GetInvocationList()
                .Cast<Action<string, IWritableState<TModel>>>()
        )
        {
            try
            {
                handler(name, state);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue state-added listener failed: {0}", exception);
            }
        }
    }

    private void NotifyRemoved(string name)
    {
        if (StateRemoved is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(name);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue state-removed listener failed: {0}", exception);
            }
        }
    }

    private static void AddCleanupError(List<Exception> errors, Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var innerException in aggregate.Flatten().InnerExceptions)
            {
                AddCleanupError(errors, exception: innerException);
            }

            return;
        }

        if (!errors.Any(existing => ReferenceEquals(existing, exception)))
        {
            errors.Add(exception);
        }
    }

    private static void AddCleanupError(ref List<Exception>? errors, Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var innerException in aggregate.Flatten().InnerExceptions)
            {
                AddCleanupError(ref errors, exception: innerException);
            }

            return;
        }

        errors ??= [];
        if (!errors.Any(existing => ReferenceEquals(existing, exception)))
        {
            errors.Add(exception);
        }
    }

    private static void ValidateName(string stateName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
