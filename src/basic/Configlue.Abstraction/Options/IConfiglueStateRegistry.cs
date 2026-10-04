namespace Configlue;

/// <summary>Manages dynamic named state instances for one model at runtime.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
/// <remarks>
/// <para>
/// Each named state instance is addressed by <c>(TModel, StateName)</c> and owns an independent
/// runtime. The registry is a materialization cache, not a persistence mechanism: removing a
/// state unloads its runtime, while persisted profile membership is owned by
/// <see cref="ConfiglueProfileCatalog"/> and exposed through <see cref="IConfiglueProfiledState{TModel}"/>.
/// </para>
/// <para>
/// Subjects never select state instances. A subject scopes operations inside one state instance
/// and affects provider addressing; see <see cref="ISubjectState{T}"/>.
/// </para>
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
public interface IConfiglueStateRegistry<T> : IAsyncDisposable
{
    /// <summary>The registered state names (state-instance identities).</summary>
    IReadOnlyCollection<string> StateNames { get; }

    /// <summary>Gets a registered state instance or throws when the name is unknown.</summary>
    IWritableState<T> Get(string stateName);

    /// <summary>Tries to retrieve a registered state instance.</summary>
    bool TryGet(string stateName, out IWritableState<T>? state);

    /// <summary>Creates and registers a state instance if its name is not already in use.</summary>
    /// <remarks>Waits asynchronously for its add notification unless called reentrantly or while notifications are deferred.</remarks>
    ValueTask<bool> TryAddAsync(string stateName);

    /// <summary>Removes a state instance and waits for its runtime, watchers, and notification to complete.</summary>
    ValueTask<bool> TryRemoveAsync(string stateName);

    /// <summary>Removes every state instance and waits for its runtimes, watchers, and notifications to complete.</summary>
    ValueTask ClearAsync();

    /// <summary>Raised after a state instance is registered.</summary>
    event Action<string, IWritableState<T>>? StateAdded;

    /// <summary>Raised after a state instance is removed.</summary>
    event Action<string>? StateRemoved;
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
