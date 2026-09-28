using System.Diagnostics;

namespace Configlue;

public sealed partial class ConfiglueProfiledOptions<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private sealed class ActiveProfileValueSubscription : IDisposable
    {
        private readonly ConfiglueProfiledOptions<TModel, TFragment> _owner;
        private readonly Action<TModel> _listener;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly List<Task> _pendingTasks = [];
        private readonly TaskCompletionSource _detached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly Queue<(long Generation, TModel Value)> _notifications = new();
        private IWritableOptions<TModel>? _profile;
        private IDisposable? _profileSubscription;
        private long _generation;
        private long _valueVersion;
        private bool _disposed;
        private bool _drainingNotifications;
        private Task? _completion;

        public ActiveProfileValueSubscription(
            ConfiglueProfiledOptions<TModel, TFragment> owner,
            Action<TModel> listener
        )
        {
            _owner = owner;
            _listener = listener;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _owner.ActiveProfileChanged += OnActiveProfileChanged;
                var generation = ++_generation;
                StartTask(() => InitializeAsync(generation));
            }
        }

        public void Dispose()
        {
            IDisposable? subscription;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                ++_generation;
                subscription = _profileSubscription;
                _profileSubscription = null;
                _profile = null;
                _notifications.Clear();
                _notifications.Clear();
                _owner.ActiveProfileChanged -= OnActiveProfileChanged;
                // Cancellation can invoke arbitrary source callbacks, so perform it outside the lock.
                _completion = CompleteDisposalAsync(_pendingTasks.ToArray());
            }
            try
            {
                _cancellation.Cancel();
            }
            finally
            {
                try
                {
                    subscription?.Dispose();
                }
                finally
                {
                    _detached.TrySetResult();
                }
            }
        }

        public Task WaitForCompletionAsync()
        {
            lock (_gate)
            {
                return _completion ?? Task.CompletedTask;
            }
        }

        private async Task CompleteDisposalAsync(Task[] tasks)
        {
            await _detached.Task.ConfigureAwait(false);
            await Task.WhenAll(tasks).ConfigureAwait(false);
            _cancellation.Dispose();
            _owner.RemoveSubscription(this);
        }

        private void StartTask(Func<Task> operation)
        {
            _pendingTasks.RemoveAll(static task => task.IsCompleted);
            // The synchronous notification boundary never runs source I/O inline.
            _pendingTasks.Add(
                Task.Run(
                    async () =>
                    {
                        try
                        {
                            await operation().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                            when (_cancellation.IsCancellationRequested)
                        {
                            // Disposing the subscription cancels its outstanding source operations.
                        }
                        catch (ObjectDisposedException)
                            when (Volatile.Read(ref _owner._disposed) != 0)
                        {
                            // Owner teardown can dispose a source while a binding is completing.
                        }
                        catch (Exception exception)
                        {
                            Trace.TraceError(
                                "Configlue active-profile value subscription failed: {0}",
                                exception
                            );
                        }
                    },
                    CancellationToken.None
                )
            );
        }

        private async Task InitializeAsync(long generation)
        {
            var name = await _owner
                .GetActiveProfileNameAsync(_cancellation.Token)
                .ConfigureAwait(false);
            await BindAsync(name, generation, notify: false).ConfigureAwait(false);
        }

        private void OnActiveProfileChanged(string name)
        {
            IDisposable? previous;
            lock (_gate)
            {
                if (_disposed || Volatile.Read(ref _owner._disposed) != 0)
                {
                    return;
                }
                var generation = ++_generation;
                previous = _profileSubscription;
                _profileSubscription = null;
                _profile = null;
                StartTask(() => BindAsync(name, generation, notify: true));
            }
            previous?.Dispose();
        }

        private bool IsCurrent(long generation) =>
            !_disposed && Volatile.Read(ref _owner._disposed) == 0 && generation == _generation;

        private async Task BindAsync(string name, long generation, bool notify)
        {
            lock (_gate)
            {
                if (!IsCurrent(generation))
                {
                    return;
                }
            }
            var profile = await _owner
                .GetProfileAsync(name, _cancellation.Token)
                .ConfigureAwait(false);
            long valueVersion;
            lock (_gate)
            {
                if (!IsCurrent(generation))
                {
                    return;
                }
                _profile = profile;
                valueVersion = _valueVersion;
            }
            var subscription = profile.OnChange(value =>
                OnProfileValueChanged(generation, profile, value)
            );
            bool attached;
            lock (_gate)
            {
                attached = IsCurrent(generation);
                if (attached)
                {
                    _profileSubscription = subscription;
                }
            }
            if (!attached)
            {
                subscription.Dispose();
                return;
            }
            if (notify)
            {
                var value = await profile.GetValueAsync(_cancellation.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (IsCurrent(generation) && valueVersion == _valueVersion)
                    {
                        QueueNotification(generation, value);
                    }
                }
            }
        }

        private void OnProfileValueChanged(
            long generation,
            IWritableOptions<TModel> profile,
            TModel value
        )
        {
            lock (_gate)
            {
                if (!IsCurrent(generation) || !ReferenceEquals(_profile, profile))
                {
                    return;
                }
                ++_valueVersion;
                QueueNotification(generation, value);
            }
        }

        private void QueueNotification(long generation, TModel value)
        {
            _notifications.Enqueue((generation, value));
            if (!_drainingNotifications)
            {
                _drainingNotifications = true;
                // Notification dispatch never holds up a binding task. A listener may dispose
                // the owner synchronously without waiting for the task invoking that listener.
                _ = Task.Run(DrainNotifications, CancellationToken.None);
            }
        }

        private void DrainNotifications()
        {
            while (true)
            {
                TModel value;
                lock (_gate)
                {
                    if (_notifications.Count == 0)
                    {
                        _drainingNotifications = false;
                        return;
                    }
                    var notification = _notifications.Dequeue();
                    if (!IsCurrent(notification.Generation))
                    {
                        continue;
                    }
                    value = notification.Value;
                }
                // User callbacks run outside internal locks. A callback already dispatched
                // may finish during disposal; queued or late source values are suppressed.
                NotifyListener(value);
            }
        }

        private void NotifyListener(TModel value)
        {
            try
            {
                _listener(value);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue active-profile value listener failed: {0}", exception);
            }
        }
    }
}
