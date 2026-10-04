namespace Configlue;

/// <summary>
/// Owns runtime lifetime: disposal state, active-operation tracking, and the gate
/// that makes listener/watcher registration atomic with shutdown.
///
/// The gate (Register/Unregister/ExecuteUnderGate/TrySnapshot/BeginShutdown) is the
/// only lock shared with other coordinators. Watch registration and shutdown both run
/// through it, so a watcher is either observed by shutdown or rejected because
/// shutdown already began. Listener lists themselves are owned by
/// <c>RuntimeWatchCoordinator</c>; only the lock lives here.
/// </summary>
internal sealed class RuntimeLifetime
{
    private readonly object _gate = new();
    private readonly object? _owner;
    private int _activeOperations;
    private TaskCompletionSource? _operationsDrained;
    private bool _disposed;

    internal RuntimeLifetime(object? owner = null)
    {
        _owner = owner;
    }

    internal bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>Enters one tracked operation; throws once shutdown has begun.</summary>
    internal OperationLease EnterOperation()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, _owner ?? this);
            _activeOperations++;
        }

        return new OperationLease(this);
    }

    /// <summary>Runs registration work atomically with shutdown; throws if disposed.</summary>
    internal T Register<T>(Func<T> factory)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, _owner ?? this);
            return factory();
        }
    }

    /// <summary>Runs registration work atomically with shutdown; throws if disposed.</summary>
    internal void Register(Action action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, _owner ?? this);
            action();
        }
    }

    /// <summary>Runs unregistration work under the gate; allowed after disposal.</summary>
    internal void Unregister(Action action)
    {
        lock (_gate)
        {
            action();
        }
    }

    /// <summary>Runs shutdown work under the gate without throwing when disposed.</summary>
    internal void ExecuteUnderGate(Action action)
    {
        lock (_gate)
        {
            action();
        }
    }

    /// <summary>
    /// Copies a listener list only when the runtime is still alive.
    /// Used by notification paths that silently skip delivery after disposal.
    /// </summary>
    internal bool TrySnapshot<T>(List<T> source, out T[] snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                snapshot = [];
                return false;
            }

            snapshot = source.ToArray();
            return true;
        }
    }

    /// <summary>
    /// Begins shutdown exactly once. Returns false when shutdown already began,
    /// in which case callers must not touch shutdown-owned resources again.
    /// </summary>
    internal bool TryBeginShutdown(out Task operationsDrained)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                operationsDrained = Task.CompletedTask;
                return false;
            }

            _disposed = true;
            if (_activeOperations == 0)
            {
                operationsDrained = Task.CompletedTask;
                return true;
            }

            _operationsDrained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            operationsDrained = _operationsDrained.Task;
            return true;
        }
    }

    private void ExitOperation()
    {
        lock (_gate)
        {
            if (--_activeOperations == 0)
            {
                _operationsDrained?.TrySetResult();
            }
        }
    }

    internal readonly struct OperationLease(RuntimeLifetime? owner) : IDisposable
    {
        public void Dispose() => owner?.ExitOperation();
    }
}
