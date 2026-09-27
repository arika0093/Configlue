using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Configlue;

internal sealed class ConfiglueFacadeOptionsRegistry<TModel>
    : IConfiglueOptionsRegistry<TModel>,
        IConfiglueOptionsRegistryNotificationDeferrer<TModel>
    where TModel : IConfiglueFacadeModel<TModel>
{
    private sealed record Entry
    {
        public IWritableOptions<TModel> Runtime { get; init; }
        public IDisposable[] Resources { get; init; }

        public Entry(IWritableOptions<TModel> Runtime, IDisposable[] Resources)
        {
            this.Runtime = Runtime;
            this.Resources = Resources;
        }

        public void Deconstruct(out IWritableOptions<TModel> Runtime, out IDisposable[] Resources)
        {
            Runtime = this.Runtime;
            Resources = this.Resources;
        }
    }

    private sealed class Notification(IWritableOptions<TModel> runtime, Action dispatch)
    {
        public IWritableOptions<TModel> Runtime { get; } = runtime;
        public Action Dispatch { get; } = dispatch;
        public bool IsCancelled { get; set; }
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Func<string, Entry> _factory;
    private readonly HashSet<string> _reservedNames;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retiringNames = new(StringComparer.Ordinal);
    private readonly HashSet<Task<Exception?>> _pendingRemovals = [];
    private readonly Queue<Notification> _notifications = new();
    private readonly AsyncLocal<bool> _insideNotification = new();
    private Notification? _activeNotification;
    private Task? _disposeTask;
    private bool _dispatchingNotifications;
    private int _notificationDeferralCount;
    private bool _disposed;

    public ConfiglueFacadeOptionsRegistry(
        Func<string, (IWritableOptions<TModel> Runtime, IDisposable[] Resources)> factory,
        IEnumerable<string> reservedNames
    )
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = name =>
        {
            var created = factory(name);
            return new Entry(created.Runtime, created.Resources);
        };
        _reservedNames = new HashSet<string>(reservedNames, StringComparer.Ordinal);
    }

    public event Action<string, IWritableOptions<TModel>>? ProfileAdded;
    public event Action<string>? ProfileRemoved;

    public IReadOnlyCollection<string> ProfileNames
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

    public IWritableOptions<TModel> Get(string profileName)
    {
        ValidateName(profileName);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _entries.TryGetValue(profileName, out var entry)
                ? entry.Runtime
                : throw new KeyNotFoundException(
                    $"Configlue options '{profileName}' is not registered dynamically."
                );
        }
    }

    public bool TryGet(string profileName, out IWritableOptions<TModel>? options)
    {
        ValidateName(profileName);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(profileName, out var entry))
            {
                options = entry.Runtime;
                return true;
            }
            options = null;
            return false;
        }
    }

    internal IReadOnlyList<IDisposable> GetOwnedResourcesForTests(string profileName)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _entries.TryGetValue(profileName, out var entry)
                ? Array.AsReadOnly(entry.Resources.ToArray())
                : throw new KeyNotFoundException(
                    $"Configlue options '{profileName}' is not registered dynamically."
                );
        }
    }

    public bool TryAdd(string profileName)
    {
        ValidateName(profileName);
        Entry entry;
        Notification notification;
        bool waitForNotifications;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (
                _reservedNames.Contains(profileName)
                || _entries.ContainsKey(profileName)
                || _retiringNames.Contains(profileName)
            )
            {
                return false;
            }
            entry = _factory(profileName);
            _entries.Add(profileName, entry);
            notification = new Notification(
                entry.Runtime,
                () => NotifyAdded(profileName, entry.Runtime)
            );
            _notifications.Enqueue(notification);
            waitForNotifications = !_insideNotification.Value && _notificationDeferralCount == 0;
        }
        DrainNotifications();
        WaitForNotifications([notification], waitForNotifications).GetAwaiter().GetResult();
        return true;
    }

    public bool TryRemove(string profileName) =>
        TryRemoveAsync(profileName).AsTask().GetAwaiter().GetResult();

    public async ValueTask<bool> TryRemoveAsync(string profileName)
    {
        ValidateName(profileName);
        Entry? entry;
        var completed = new TaskCompletionSource<Exception?>(
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
            if (!_entries.Remove(profileName, out entry))
            {
                return false;
            }
            _retiringNames.Add(profileName);
            _pendingRemovals.Add(completed.Task);
            notification = new Notification(
                entry.Runtime,
                () =>
                {
                    notificationReady.Task.GetAwaiter().GetResult();
                    NotifyRemoved(profileName);
                }
            );
            _notifications.Enqueue(notification);
            waitForNotifications = !_insideNotification.Value && _notificationDeferralCount == 0;
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
        finally
        {
            lock (_gate)
            {
                _retiringNames.Remove(profileName);
                _pendingRemovals.Remove(completed.Task);
                notificationReady.TrySetResult();
                completed.TrySetResult(disposalError);
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

    public void Clear() => ClearAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask ClearAsync()
    {
        KeyValuePair<string, Entry>[] removed;
        Task<Exception?>[] pendingBeforeClear;
        bool waitForNotifications;
        Notification[] notificationsToAwait;
        var completed = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var notificationReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        lock (_gate)
        {
            ThrowIfDisposed();
            removed = _entries.ToArray();
            _entries.Clear();
            foreach (var (name, entry) in removed)
            {
                _retiringNames.Add(name);
                _notifications.Enqueue(
                    new Notification(
                        entry.Runtime,
                        () =>
                        {
                            notificationReady.Task.GetAwaiter().GetResult();
                            NotifyRemoved(name);
                        }
                    )
                );
            }
            notificationsToAwait = CaptureQueuedNotificationsLocked();
            waitForNotifications = !_insideNotification.Value && _notificationDeferralCount == 0;
            pendingBeforeClear = _insideNotification.Value ? [] : _pendingRemovals.ToArray();
            if (removed.Length > 0)
            {
                _pendingRemovals.Add(completed.Task);
            }
        }
        if (removed.Length == 0)
        {
            if (pendingBeforeClear.Length > 0)
            {
                var pendingErrors = await Task.WhenAll(pendingBeforeClear).ConfigureAwait(false);
                var failures = pendingErrors.OfType<Exception>().ToArray();
                if (failures.Length > 0)
                {
                    throw new AggregateException(
                        "One or more dynamic Configlue options failed to clear.",
                        failures
                    );
                }
            }
            await WaitForNotifications(notificationsToAwait, waitForNotifications)
                .ConfigureAwait(false);
            return;
        }
        List<Exception>? errors = null;
        Exception? clearFailure = null;
        try
        {
            if (pendingBeforeClear.Length > 0)
            {
                try
                {
                    var pendingErrors = await Task.WhenAll(pendingBeforeClear)
                        .ConfigureAwait(false);
                    foreach (var pendingError in pendingErrors.OfType<Exception>())
                    {
                        AddCleanupError(ref errors, pendingError);
                    }
                }
                catch (Exception exception)
                {
                    AddCleanupError(ref errors, exception);
                }
            }
            try
            {
                await DisposeEntriesAsync(removed).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddCleanupError(ref errors, exception);
            }
        }
        finally
        {
            lock (_gate)
            {
                foreach (var (name, _) in removed)
                {
                    _retiringNames.Remove(name);
                }
                _pendingRemovals.Remove(completed.Task);
                notificationReady.TrySetResult();
                clearFailure = errors is null
                    ? null
                    : new AggregateException(
                        "One or more dynamic Configlue options failed to clear.",
                        errors
                    );
                completed.TrySetResult(clearFailure);
            }
            DrainNotifications();
        }
        await WaitForNotifications(notificationsToAwait, waitForNotifications)
            .ConfigureAwait(false);
        if (errors is not null)
        {
            throw new AggregateException(
                "One or more dynamic Configlue options failed to clear.",
                errors
            );
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        KeyValuePair<string, Entry>[] removed;
        Task<Exception?>[] pendingRemovals;
        TaskCompletionSource completion;
        TaskCompletionSource notificationReady;
        Notification[] notificationsToAwait;
        bool waitForNotifications;
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_insideNotification.Value ? Task.CompletedTask : _disposeTask);
            }
            _disposed = true;
            removed = _entries.ToArray();
            _entries.Clear();
            notificationReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            foreach (var (name, entry) in removed)
            {
                _retiringNames.Add(name);
                _notifications.Enqueue(
                    new Notification(
                        entry.Runtime,
                        () =>
                        {
                            notificationReady.Task.GetAwaiter().GetResult();
                            NotifyRemoved(name);
                        }
                    )
                );
            }
            notificationsToAwait = CaptureQueuedNotificationsLocked();
            waitForNotifications = !_insideNotification.Value && _notificationDeferralCount == 0;
            pendingRemovals = _insideNotification.Value ? [] : _pendingRemovals.ToArray();
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _disposeTask = completion.Task;
        }
        _ = FinishDisposeAsync(
            pendingRemovals,
            removed,
            notificationsToAwait,
            waitForNotifications,
            notificationReady,
            completion
        );
        return new ValueTask(completion.Task);
    }

    private async Task FinishDisposeAsync(
        Task<Exception?>[] pendingRemovals,
        KeyValuePair<string, Entry>[] removed,
        Notification[] notificationsToAwait,
        bool waitForNotifications,
        TaskCompletionSource notificationReady,
        TaskCompletionSource completion
    )
    {
        List<Exception>? errors = null;
        try
        {
            if (pendingRemovals.Length > 0)
            {
                try
                {
                    var pendingErrors = await Task.WhenAll(pendingRemovals).ConfigureAwait(false);
                    foreach (var pendingError in pendingErrors.OfType<Exception>())
                    {
                        AddCleanupError(ref errors, pendingError);
                    }
                }
                catch (Exception exception)
                {
                    AddCleanupError(ref errors, exception);
                }
            }
            try
            {
                await DisposeEntriesAsync(removed).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddCleanupError(ref errors, exception);
            }
            lock (_gate)
            {
                foreach (var (name, _) in removed)
                {
                    _retiringNames.Remove(name);
                }
                notificationReady.TrySetResult();
            }
            if (errors is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(
                    new AggregateException(
                        "One or more dynamic Configlue options failed to dispose.",
                        errors
                    )
                );
            }
            DrainNotifications();
            await WaitForNotifications(notificationsToAwait, waitForNotifications)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static async ValueTask DisposeEntriesAsync(KeyValuePair<string, Entry>[] entries)
    {
        List<Exception>? errors = null;
        foreach (var (_, entry) in entries)
        {
            try
            {
                await DisposeEntryAsync(entry).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AddCleanupError(ref errors, exception);
            }
        }
        if (errors is not null)
        {
            throw new AggregateException(
                "One or more dynamic Configlue options failed to dispose.",
                errors
            );
        }
    }

    private static async ValueTask DisposeEntryAsync(Entry entry)
    {
        List<Exception>? errors = null;
        try
        {
            if (entry.Runtime is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (entry.Runtime is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception exception)
        {
            AddCleanupError(ref errors, exception);
        }
        foreach (var resource in entry.Resources)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                AddCleanupError(ref errors, exception);
            }
        }
        if (errors is not null)
        {
            throw new AggregateException(
                "A dynamic Configlue options runtime failed to dispose.",
                errors
            );
        }
    }

    IConfiglueOptionsRegistryNotificationDeferral<TModel> IConfiglueOptionsRegistryNotificationDeferrer<TModel>.DeferNotifications()
    {
        lock (_gate)
        {
            _notificationDeferralCount++;
        }
        return new NotificationScope(this);
    }

    private void CancelNotifications(IWritableOptions<TModel> runtime)
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
                .GetAwaiter()
                .GetResult();
        }
    }

    private sealed class NotificationScope(ConfiglueFacadeOptionsRegistry<TModel> owner)
        : IConfiglueOptionsRegistryNotificationDeferral<TModel>
    {
        private ConfiglueFacadeOptionsRegistry<TModel>? _owner = owner;

        public void Cancel(IWritableOptions<TModel> runtime)
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

    private static void AddCleanupError(ref List<Exception>? errors, Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var innerException in aggregate.Flatten().InnerExceptions)
            {
                AddCleanupError(ref errors, innerException);
            }
            return;
        }

        errors ??= [];
        if (!errors.Any(existing => ReferenceEquals(existing, exception)))
        {
            errors.Add(exception);
        }
    }

    private void NotifyAdded(string name, IWritableOptions<TModel> options)
    {
        if (ProfileAdded is not { } handlers)
        {
            return;
        }
        foreach (
            var handler in handlers
                .GetInvocationList()
                .Cast<Action<string, IWritableOptions<TModel>>>()
        )
        {
            try
            {
                handler(name, options);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue profile-added listener failed: {0}", exception);
            }
        }
    }

    private void NotifyRemoved(string name)
    {
        if (ProfileRemoved is not { } handlers)
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
                Trace.TraceError("Configlue profile-removed listener failed: {0}", exception);
            }
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

    private static void ValidateName(string name) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
