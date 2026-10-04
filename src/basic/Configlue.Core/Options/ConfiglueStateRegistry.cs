using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Configlue.CompilerServices;

namespace Configlue;

/// <summary>A thread-safe registry that creates and owns named Configlue states.</summary>
/// <typeparam name="TModel">The configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
internal sealed class ConfiglueStateRegistry<TModel, TFragment>
    : IConfiglueStateRegistry<TModel>,
        IConfiglueStateRegistryNotificationDeferrer<TModel>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private sealed class Notification(
        IWritableState<TModel> runtime,
        Action dispatch,
        Task? ready = null
    )
    {
        public IWritableState<TModel> Runtime { get; } = runtime;
        public Action Dispatch { get; } = dispatch;
        public Task Ready { get; } = ready ?? Task.CompletedTask;
        public bool IsCancelled { get; set; }
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Func<string, ConfiglueRuntime<TModel, TFragment>> _factory;
    private readonly object _gate = new();
    private readonly Dictionary<string, ConfiglueRuntime<TModel, TFragment>> _states = new(
        StringComparer.Ordinal
    );
    private readonly HashSet<string> _retiringStates = new(StringComparer.Ordinal);
    private readonly HashSet<Task<Exception?>> _pendingAsyncRemovals = [];
    private readonly Queue<Notification> _notifications = new();
    private readonly AsyncLocal<bool> _insideNotification = new();
    private Notification? _activeNotification;
    private bool _dispatchingNotifications;
    private int _notificationDeferralCount;
    private Task? _disposeTask;
    private bool _disposed;

    /// <summary>Creates a registry using a factory that builds a state from its name.</summary>
    public ConfiglueStateRegistry(Func<string, ConfiglueRuntime<TModel, TFragment>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
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
                return Array.AsReadOnly(_states.Keys.ToArray());
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
            return _states.TryGetValue(stateName, out var state)
                ? state
                : throw new KeyNotFoundException(
                    $"Configlue state '{stateName}' is not registered."
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
            if (_states.TryGetValue(stateName, out var registered))
            {
                state = registered;
                return true;
            }

            state = null;
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryAddAsync(string stateName)
    {
        ValidateName(stateName);
        ConfiglueRuntime<TModel, TFragment> state;
        Notification notification;
        bool waitForNotifications;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_states.ContainsKey(stateName) || _retiringStates.Contains(stateName))
            {
                return false;
            }

            state =
                _factory(stateName)
                ?? throw new InvalidOperationException("The state factory returned null.");
            _states.Add(stateName, state);
            notification = new Notification(state, () => NotifyAdded(stateName, state));
            _notifications.Enqueue(notification);
            waitForNotifications = !_insideNotification.Value && _notificationDeferralCount == 0;
        }

        DrainNotifications();
        await WaitForNotifications([notification], waitForNotifications).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryRemoveAsync(string stateName)
    {
        ValidateName(stateName);
        ConfiglueRuntime<TModel, TFragment>? state;
        var removalCompleted = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var notificationReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Notification notification;
        bool waitForNotifications;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_states.TryGetValue(stateName, out state))
            {
                return false;
            }

            _states.Remove(stateName);
            _retiringStates.Add(stateName);
            _pendingAsyncRemovals.Add(removalCompleted.Task);
            notification = new Notification(
                state,
                () => NotifyRemoved(stateName),
                notificationReady.Task
            );
            _notifications.Enqueue(notification);
            waitForNotifications = !_insideNotification.Value && _notificationDeferralCount == 0;
        }

        Exception? disposalError = null;
        try
        {
            await state.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposalError = exception;
        }
        finally
        {
            lock (_gate)
            {
                _retiringStates.Remove(stateName);
                _pendingAsyncRemovals.Remove(removalCompleted.Task);
                notificationReady.TrySetResult();
                removalCompleted.TrySetResult(disposalError);
            }
            DrainNotifications();
        }

        await WaitForNotifications([notification], waitForNotifications).ConfigureAwait(false);

        if (disposalError is not null)
        {
            ExceptionDispatchInfo.Capture(disposalError).Throw();
        }
        return true;
    }

    /// <inheritdoc />
    public async ValueTask ClearAsync()
    {
        KeyValuePair<string, ConfiglueRuntime<TModel, TFragment>>[] removed;
        Task<Exception?>[] pendingBeforeClear;
        Notification[] notificationsToAwait;
        bool waitForNotifications;
        var clearCompleted = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var notificationReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        lock (_gate)
        {
            ThrowIfDisposed();
            removed = _states.ToArray();
            _states.Clear();
            foreach (var (name, state) in removed)
            {
                _retiringStates.Add(name);
                _notifications.Enqueue(
                    new Notification(state, () => NotifyRemoved(name), notificationReady.Task)
                );
            }
            notificationsToAwait = CaptureQueuedNotificationsLocked();
            waitForNotifications = !_insideNotification.Value && _notificationDeferralCount == 0;
            pendingBeforeClear = _insideNotification.Value ? [] : _pendingAsyncRemovals.ToArray();
            if (removed.Length > 0)
            {
                _pendingAsyncRemovals.Add(clearCompleted.Task);
            }
        }

        var disposalErrors = new List<Exception>();
        Exception? clearFailure = null;
        try
        {
            try
            {
                var pendingErrors = await Task.WhenAll(pendingBeforeClear).ConfigureAwait(false);
                foreach (var pendingError in pendingErrors.OfType<Exception>())
                {
                    AddCleanupError(disposalErrors, pendingError);
                }
            }
            catch (Exception exception)
            {
                AddCleanupError(disposalErrors, exception);
            }

            foreach (var (_, state) in removed)
            {
                try
                {
                    await state.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    AddCleanupError(disposalErrors, exception);
                }
            }

            if (disposalErrors.Count > 0)
            {
                clearFailure = new AggregateException(
                    "One or more Configlue runtimes failed to dispose.",
                    disposalErrors
                );
                throw clearFailure;
            }
        }
        finally
        {
            if (removed.Length > 0)
            {
                lock (_gate)
                {
                    foreach (var (name, _) in removed)
                    {
                        _retiringStates.Remove(name);
                    }
                    _pendingAsyncRemovals.Remove(clearCompleted.Task);
                    notificationReady.TrySetResult();
                    clearCompleted.TrySetResult(clearFailure);
                }
            }
            DrainNotifications();
        }

        await WaitForNotifications(notificationsToAwait, waitForNotifications)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        KeyValuePair<string, ConfiglueRuntime<TModel, TFragment>>[] removed;
        Task<Exception?>[] pendingRemovals;
        TaskCompletionSource notificationReady;
        TaskCompletionSource completion;
        TaskCompletionSource cleanupCompletion;
        Notification[] notificationsToAwait;
        bool waitForNotifications;
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_insideNotification.Value ? Task.CompletedTask : _disposeTask);
            }

            _disposed = true;
            removed = _states.ToArray();
            _states.Clear();
            notificationReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            foreach (var (name, state) in removed)
            {
                _retiringStates.Add(name);
                _notifications.Enqueue(
                    new Notification(state, () => NotifyRemoved(name), notificationReady.Task)
                );
            }
            notificationsToAwait = CaptureQueuedNotificationsLocked();
            waitForNotifications = !_insideNotification.Value && _notificationDeferralCount == 0;
            pendingRemovals = _insideNotification.Value ? [] : _pendingAsyncRemovals.ToArray();
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            cleanupCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _disposeTask = completion.Task;
        }

        _ = FinishDisposeAsync(
            removed,
            pendingRemovals,
            notificationsToAwait,
            notificationReady,
            waitForNotifications ? null : cleanupCompletion,
            completion
        );
        // The caller inside a notification must return so the owning drain can continue.
        // External callers share the task that also awaits the notification boundary.
        return new ValueTask(waitForNotifications ? completion.Task : cleanupCompletion.Task);
    }

    private async Task FinishDisposeAsync(
        KeyValuePair<string, ConfiglueRuntime<TModel, TFragment>>[] removed,
        Task<Exception?>[] pendingRemovals,
        Notification[] notificationsToAwait,
        TaskCompletionSource notificationReady,
        TaskCompletionSource? cleanupCompletion,
        TaskCompletionSource completion
    )
    {
        var disposalErrors = new List<Exception>();
        Exception? disposalFailure = null;
        try
        {
            foreach (var (_, state) in removed)
            {
                try
                {
                    await state.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    AddCleanupError(disposalErrors, exception);
                }
            }

            try
            {
                var pendingErrors = await Task.WhenAll(pendingRemovals).ConfigureAwait(false);
                foreach (var pendingError in pendingErrors.OfType<Exception>())
                {
                    AddCleanupError(disposalErrors, pendingError);
                }
            }
            catch (Exception exception)
            {
                AddCleanupError(disposalErrors, exception);
            }

            if (disposalErrors.Count > 0)
            {
                throw new AggregateException(
                    "One or more Configlue runtimes failed to dispose.",
                    disposalErrors
                );
            }
        }
        catch (Exception exception)
        {
            disposalFailure = exception;
        }
        finally
        {
            lock (_gate)
            {
                foreach (var (name, _) in removed)
                {
                    _retiringStates.Remove(name);
                }
                notificationReady.TrySetResult();
            }
        }

        if (disposalFailure is null)
        {
            cleanupCompletion?.TrySetResult();
        }
        else
        {
            cleanupCompletion?.TrySetException(disposalFailure);
        }

        DrainNotifications();
        await WaitForNotifications(notificationsToAwait, waitForNotifications: true)
            .ConfigureAwait(false);

        if (disposalFailure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(disposalFailure);
        }
    }

    IConfiglueStateRegistryNotificationDeferral<TModel> IConfiglueStateRegistryNotificationDeferrer<TModel>.DeferNotifications()
    {
        lock (_gate)
        {
            _notificationDeferralCount++;
        }
        return new NotificationScope(this);
    }

    private void CancelNotifications(IWritableState<TModel> runtime)
    {
        lock (_gate)
        {
            foreach (
                var notification in _notifications.Where(notification =>
                    ReferenceEquals(notification.Runtime, runtime)
                )
            )
            {
                notification.IsCancelled = true;
            }
        }
    }

    private void ReleaseNotificationDeferral()
    {
        var shouldDrain = false;
        Notification[] notificationsToAwait = [];
        var waitForNotifications = false;
        lock (_gate)
        {
            if (_notificationDeferralCount <= 0)
            {
                throw new InvalidOperationException("No notification deferral is active.");
            }
            _notificationDeferralCount--;
            shouldDrain = _notificationDeferralCount == 0;
            if (shouldDrain)
            {
                notificationsToAwait = CaptureQueuedNotificationsLocked();
                waitForNotifications = !_insideNotification.Value;
            }
        }
        if (shouldDrain)
        {
            DrainNotifications();
            WaitForNotifications(notificationsToAwait, waitForNotifications)
                // Synchronous registry API boundary preserves completed notifications and cleanup; async removal/clear/disposal are preferred.
                .GetAwaiter()
                .GetResult();
        }
    }

    private sealed class NotificationScope(ConfiglueStateRegistry<TModel, TFragment> owner)
        : IConfiglueStateRegistryNotificationDeferral<TModel>
    {
        private ConfiglueStateRegistry<TModel, TFragment>? _owner = owner;

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

    private void NotifyAdded(string name, IWritableState<TModel> state)
    {
        var handlers = StateAdded;
        if (handlers is null)
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
        var handlers = StateRemoved;
        if (handlers is null)
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
                AddCleanupError(errors, innerException);
            }
            return;
        }

        if (!errors.Any(existing => ReferenceEquals(existing, exception)))
        {
            errors.Add(exception);
        }
    }

    private void DrainNotifications()
    {
        lock (_gate)
        {
            if (_dispatchingNotifications || _notificationDeferralCount > 0)
            {
                return;
            }

            _dispatchingNotifications = true;
        }

        while (true)
        {
            Notification notification;
            lock (_gate)
            {
                if (_notifications.Count == 0 || _notificationDeferralCount > 0)
                {
                    _dispatchingNotifications = false;
                    _activeNotification = null;
                    return;
                }

                if (!_notifications.Peek().Ready.IsCompleted)
                {
                    var ready = _notifications.Peek().Ready;
                    _dispatchingNotifications = false;
                    _activeNotification = null;
                    ready.GetAwaiter().OnCompleted(DrainNotifications);
                    return;
                }

                notification = _notifications.Dequeue();
                _activeNotification = notification;
            }

            try
            {
                var wasInsideNotification = _insideNotification.Value;
                _insideNotification.Value = true;
                try
                {
                    if (!notification.IsCancelled)
                    {
                        notification.Dispatch();
                    }
                }
                finally
                {
                    _insideNotification.Value = wasInsideNotification;
                }
            }
            finally
            {
                lock (_gate)
                {
                    notification.Completion.TrySetResult();
                    _activeNotification = null;
                }
            }
        }
    }

    private Notification[] CaptureQueuedNotificationsLocked() =>
        (
            _activeNotification is null
                ? _notifications
                : _notifications.Prepend(_activeNotification)
        ).ToArray();

    private static Task WaitForNotifications(
        IEnumerable<Notification> notifications,
        bool waitForNotifications
    ) =>
        waitForNotifications
            ? Task.WhenAll(
                notifications.Select(static notification => notification.Completion.Task)
            )
            : Task.CompletedTask;

    private static void ValidateName(string stateName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(stateName);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
