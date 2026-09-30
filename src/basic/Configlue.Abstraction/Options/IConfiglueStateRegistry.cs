namespace Configlue;

/// <summary>Manages named states that can be added and removed at runtime.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
/// <remarks>
/// Notifications are delivered in state-transition order. Built-in registries wait for callbacks
/// associated with an operation's transitions before that operation completes. Calls made
/// reentrantly from a callback, or while notifications are deferred, may complete before their
/// queued callbacks; the active dispatcher or deferral scope delivers them afterward. A listener
/// exception is logged and does not prevent other listeners from receiving the notification. A
/// custom registry used by a state manager should implement
/// <see cref="IConfiglueStateRegistryNotificationDeferrer{T}"/> when its listeners reenter that
/// manager. Without that capability, callbacks may run synchronously while the manager is in an
/// operation, so listeners must not synchronously wait for another manager operation.
/// </remarks>
public interface IConfiglueStateRegistry<T> : IDisposable, IAsyncDisposable
{
    /// <summary>The currently registered state names.</summary>
    IReadOnlyCollection<string> StateNames { get; }

    /// <summary>Gets a registered state or throws when the name is unknown.</summary>
    IWritableState<T> Get(string stateName);

    /// <summary>Tries to retrieve a registered state.</summary>
    bool TryGet(string stateName, out IWritableState<T>? state);

    /// <summary>Creates and registers a state if its name is not already in use.</summary>
    /// <remarks>Waits for its add notification unless called reentrantly or while notifications are deferred.</remarks>
    bool TryAdd(string stateName);

    /// <summary>Removes a state and waits for its runtime, watchers, and notification to complete.</summary>
    bool TryRemove(string stateName);

    /// <summary>Removes every state and waits for its runtimes, watchers, and notifications to complete.</summary>
    void Clear();

    /// <summary>Raised after a state is registered.</summary>
    event Action<string, IWritableState<T>>? StateAdded;

    /// <summary>Raised after a state is removed.</summary>
    event Action<string>? StateRemoved;
}

/// <summary>Provides asynchronous cleanup for a state registry when supported.</summary>
public interface IAsyncConfiglueStateRegistry<T> : IConfiglueStateRegistry<T>
{
    /// <summary>Removes a state and waits for its runtime, watchers, and notification to complete.</summary>
    ValueTask<bool> TryRemoveAsync(string stateName);

    /// <summary>Removes every state and waits for its runtimes, watchers, and notifications to complete.</summary>
    ValueTask ClearAsync();
}

/// <summary>Allows a registry to defer notifications while a state-manager operation is in progress.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
/// <remarks>
/// Implement this capability when registry event listeners can reenter a state manager that
/// uses the registry. The manager disposes the returned scope after releasing its own operation
/// gate, allowing queued callbacks to query the manager without deadlocking. Implementations
/// should preserve FIFO order and support nested scopes; notifications resume when the last scope
/// is disposed.
/// </remarks>
public interface IConfiglueStateRegistryNotificationDeferrer<T>
{
    /// <summary>Defers queued registry notifications until the returned scope is disposed.</summary>
    IConfiglueStateRegistryNotificationDeferral<T> DeferNotifications();
}

/// <summary>A scope that defers registry notifications and can cancel notifications for a runtime.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
/// <remarks>Disposal releases one deferral scope. Cancellation applies to queued events for the exact runtime instance.</remarks>
public interface IConfiglueStateRegistryNotificationDeferral<T> : IDisposable
{
    /// <summary>Cancels queued notifications associated with this exact runtime instance.</summary>
    void Cancel(IWritableState<T> runtime);
}
