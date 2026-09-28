namespace Configlue.Extensions.Reactive;

internal sealed class CallbackSubscription<T> : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Notification> _pending = new();
    private readonly Action<T> _next;
    private readonly Action<Exception> _error;
    private readonly CancellationTokenSource? _cancellation;
    private readonly CancellationToken _token;
    private IDisposable? _listener;
    private long _version;
    private bool _draining;
    private bool _stopped;
    private bool _disposed;

    private CallbackSubscription(Action<T> next, Action<Exception> error, bool readInitial)
    {
        _next = next;
        _error = error;
        if (readInitial)
        {
            _cancellation = new CancellationTokenSource();
            _token = _cancellation.Token;
        }
    }

    public static IDisposable Start(
        Func<Action<T>, IDisposable> subscribe,
        Func<CancellationToken, ValueTask<T>>? readInitial,
        Action<T> next,
        Action<Exception> error
    )
    {
        var subscription = new CallbackSubscription<T>(next, error, readInitial is not null);
        try
        {
            // Capture before attaching: attaching is allowed to call the listener synchronously.
            var listener = subscribe(subscription.Changed);
            var discard = false;
            lock (subscription._gate)
            {
                discard = subscription._disposed;
                if (!discard)
                {
                    subscription._listener = listener;
                }
            }
            if (discard)
            {
                listener.Dispose();
            }
            if (readInitial is not null && !discard)
            {
                _ = subscription.ReadInitialAsync(readInitial);
            }
        }
        catch (Exception exception)
        {
            subscription.Fail(exception);
        }
        return subscription;
    }

    private async Task ReadInitialAsync(Func<CancellationToken, ValueTask<T>> read)
    {
        try
        {
            T value;
            try
            {
                value = await read(_token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Fail(exception);
                return;
            }
            Enqueue(value, initial: true);
        }
        catch (Exception exception)
        {
            // There is no publisher stack on which to propagate an asynchronous observer failure.
            // Detach and report the consumer failure instead of leaving a faulted background task.
            Dispose();
            global::System.Diagnostics.Trace.TraceError(
                "Configlue initial observable notification failed: {0}",
                exception
            );
        }
    }

    private void Changed(T value) => Enqueue(value, initial: false);

    private void Enqueue(T value, bool initial)
    {
        var drain = false;
        lock (_gate)
        {
            if (_disposed || _stopped || initial && _version != 0)
            {
                return;
            }
            if (!initial)
            {
                _version++;
            }
            _pending.Enqueue(new Notification { Value = value });
            if (!_draining)
            {
                _draining = drain = true;
            }
        }
        if (drain)
        {
            Drain();
        }
    }

    private void Fail(Exception exception)
    {
        var drain = false;
        lock (_gate)
        {
            if (_disposed || _stopped)
            {
                return;
            }
            _stopped = true;
            _pending.Enqueue(new Notification { Error = exception });
            if (!_draining)
            {
                _draining = drain = true;
            }
        }
        if (drain)
        {
            Drain();
        }
    }

    private void Drain()
    {
        while (true)
        {
            Notification notification;
            lock (_gate)
            {
                if (_disposed || _pending.Count == 0)
                {
                    _draining = false;
                    return;
                }
                notification = _pending.Dequeue();
            }
            try
            {
                if (notification.Error is not null)
                {
                    try
                    {
                        _error(notification.Error);
                    }
                    finally
                    {
                        Dispose();
                    }
                    return;
                }
                _next(notification.Value!);
            }
            catch
            {
                Dispose();
                throw;
            }
        }
    }

    public void Dispose()
    {
        IDisposable? listener;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _pending.Clear();
            listener = _listener;
            _listener = null;
        }
        try
        {
            listener?.Dispose();
        }
        finally
        {
            try
            {
                _cancellation?.Cancel();
            }
            finally
            {
                _cancellation?.Dispose();
            }
        }
    }

    private readonly record struct Notification
    {
        public T? Value { get; init; }
        public Exception? Error { get; init; }
    }
}
