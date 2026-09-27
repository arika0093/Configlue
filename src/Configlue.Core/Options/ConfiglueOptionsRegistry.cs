using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Configlue;

/// <summary>A thread-safe registry that creates and owns named Configlue options profiles.</summary>
/// <typeparam name="TModel">The configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
public sealed class ConfiglueOptionsRegistry<TModel, TFragment> : IConfiglueOptionsRegistry<TModel>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly Func<string, ConfiglueOptions<TModel, TFragment>> _factory;
    private readonly object _gate = new();
    private readonly Dictionary<string, ConfiglueOptions<TModel, TFragment>> _profiles = new(
        StringComparer.Ordinal
    );
    private readonly HashSet<string> _retiringProfiles = new(StringComparer.Ordinal);
    private readonly HashSet<Task<Exception?>> _pendingAsyncRemovals = [];
    private readonly Queue<Action> _notifications = new();
    private bool _dispatchingNotifications;
    private Task? _disposeTask;
    private bool _disposed;

    /// <summary>Creates a registry using a factory that builds a profile from its name.</summary>
    public ConfiglueOptionsRegistry(Func<string, ConfiglueOptions<TModel, TFragment>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc />
    public event Action<string, IWritableOptions<TModel>>? ProfileAdded;

    /// <inheritdoc />
    public event Action<string>? ProfileRemoved;

    /// <inheritdoc />
    public IReadOnlyCollection<string> ProfileNames
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_profiles.Keys.ToArray());
            }
        }
    }

    /// <inheritdoc />
    public IWritableOptions<TModel> Get(string profileName)
    {
        ValidateName(profileName);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _profiles.TryGetValue(profileName, out var options)
                ? options
                : throw new KeyNotFoundException(
                    $"Configlue profile '{profileName}' is not registered."
                );
        }
    }

    /// <inheritdoc />
    public bool TryGet(string profileName, out IWritableOptions<TModel>? options)
    {
        ValidateName(profileName);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_profiles.TryGetValue(profileName, out var registered))
            {
                options = registered;
                return true;
            }

            options = null;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryAdd(string profileName)
    {
        ValidateName(profileName);
        ConfiglueOptions<TModel, TFragment> options;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_profiles.ContainsKey(profileName) || _retiringProfiles.Contains(profileName))
            {
                return false;
            }

            options =
                _factory(profileName)
                ?? throw new InvalidOperationException("The profile factory returned null.");
            _profiles.Add(profileName, options);
            _notifications.Enqueue(() => NotifyAdded(profileName, options));
        }

        DrainNotifications();
        return true;
    }

    /// <inheritdoc />
    public bool TryRemove(string profileName) =>
        TryRemoveAsync(profileName).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask<bool> TryRemoveAsync(string profileName)
    {
        ValidateName(profileName);
        ConfiglueOptions<TModel, TFragment>? options;
        var removalCompleted = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var notificationReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_profiles.Remove(profileName, out options))
            {
                return false;
            }

            _retiringProfiles.Add(profileName);
            _pendingAsyncRemovals.Add(removalCompleted.Task);
            _notifications.Enqueue(() =>
            {
                notificationReady.Task.GetAwaiter().GetResult();
                NotifyRemoved(profileName);
            });
        }

        Exception? disposalError = null;
        try
        {
            await options.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposalError = exception;
        }
        finally
        {
            lock (_gate)
            {
                _retiringProfiles.Remove(profileName);
                _pendingAsyncRemovals.Remove(removalCompleted.Task);
                notificationReady.TrySetResult();
                removalCompleted.TrySetResult(disposalError);
            }
            DrainNotifications();
        }

        if (disposalError is not null)
        {
            ExceptionDispatchInfo.Capture(disposalError).Throw();
        }
        return true;
    }

    /// <inheritdoc />
    public void Clear() => ClearAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask ClearAsync()
    {
        KeyValuePair<string, ConfiglueOptions<TModel, TFragment>>[] removed;
        Task<Exception?>[] pendingBeforeClear;
        var clearCompleted = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var notificationReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        lock (_gate)
        {
            ThrowIfDisposed();
            removed = _profiles.ToArray();
            _profiles.Clear();
            foreach (var (name, _) in removed)
            {
                _retiringProfiles.Add(name);
                _notifications.Enqueue(() =>
                {
                    notificationReady.Task.GetAwaiter().GetResult();
                    NotifyRemoved(name);
                });
            }
            pendingBeforeClear = _pendingAsyncRemovals.ToArray();
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

            foreach (var (_, options) in removed)
            {
                try
                {
                    await options.DisposeAsync().ConfigureAwait(false);
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
                        _retiringProfiles.Remove(name);
                    }
                    _pendingAsyncRemovals.Remove(clearCompleted.Task);
                    notificationReady.TrySetResult();
                    clearCompleted.TrySetResult(clearFailure);
                }
            }
            DrainNotifications();
        }
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        KeyValuePair<string, ConfiglueOptions<TModel, TFragment>>[] removed;
        Task<Exception?>[] pendingRemovals;
        TaskCompletionSource notificationReady;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            removed = _profiles.ToArray();
            _profiles.Clear();
            notificationReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            foreach (var (name, _) in removed)
            {
                _retiringProfiles.Add(name);
                _notifications.Enqueue(() =>
                {
                    notificationReady.Task.GetAwaiter().GetResult();
                    NotifyRemoved(name);
                });
            }
            pendingRemovals = _pendingAsyncRemovals.ToArray();
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _disposeTask = completion.Task;
        }

        _ = FinishDisposeAsync(removed, pendingRemovals, notificationReady, completion);
        return new ValueTask(completion.Task);
    }

    private async Task FinishDisposeAsync(
        KeyValuePair<string, ConfiglueOptions<TModel, TFragment>>[] removed,
        Task<Exception?>[] pendingRemovals,
        TaskCompletionSource notificationReady,
        TaskCompletionSource completion
    )
    {
        var disposalErrors = new List<Exception>();
        Exception? disposalFailure = null;
        try
        {
            foreach (var (_, options) in removed)
            {
                try
                {
                    await options.DisposeAsync().ConfigureAwait(false);
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
                    _retiringProfiles.Remove(name);
                }
                notificationReady.TrySetResult();
            }
        }

        if (disposalFailure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(disposalFailure);
        }
        DrainNotifications();
    }

    private void NotifyAdded(string name, IWritableOptions<TModel> options)
    {
        var handlers = ProfileAdded;
        if (handlers is null)
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
        var handlers = ProfileRemoved;
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
                Trace.TraceError("Configlue profile-removed listener failed: {0}", exception);
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
            if (_dispatchingNotifications)
            {
                return;
            }

            _dispatchingNotifications = true;
        }

        while (true)
        {
            Action notification;
            lock (_gate)
            {
                if (_notifications.Count == 0)
                {
                    _dispatchingNotifications = false;
                    return;
                }

                notification = _notifications.Dequeue();
            }

            notification();
        }
    }

    private static void ValidateName(string profileName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
