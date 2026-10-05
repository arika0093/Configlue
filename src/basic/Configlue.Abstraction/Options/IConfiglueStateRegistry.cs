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
/// Notifications are delivered for an operation's own transitions. Built-in registries
/// apply the mutation and dispose retired runtimes first, then deliver that operation's
/// callbacks synchronously before the operation completes (unless notifications are
/// deferred, in which case delivery waits for the deferral scope). There is no global
/// ordering across concurrent operations, and an operation never waits for another
/// operation's callbacks. A listener exception is logged and does not prevent other
/// listeners from receiving the notification. Listeners must be quick and must not
/// synchronously wait for registry operations. A custom registry used by a state manager
/// should implement <see cref="IConfiglueStateRegistryNotificationDeferrer{T}"/> when its
/// listeners reenter that manager. Without that capability, callbacks run while the manager
/// is in an operation, so listeners must not synchronously wait for another manager
/// operation.
/// Advanced application API for dynamic named states.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueStateRegistry<T> : IAsyncDisposable
{
    /// <summary>The registered state names (state-instance identities).</summary>
    IReadOnlyCollection<string> StateNames { get; }

    /// <summary>Gets a registered state instance or throws when the name is unknown.</summary>
    IWritableState<T> Get(string stateName);

    /// <summary>Tries to retrieve a registered state instance.</summary>
    bool TryGet(string stateName, out IWritableState<T>? state);

    /// <summary>Creates and registers a state instance if its name is not already in use.</summary>
    /// <remarks>Delivers its add notification before completing unless notifications are deferred.</remarks>
    ValueTask<bool> TryAddAsync(string stateName);

    /// <summary>Removes a state instance, disposes its runtime and owned resources, then delivers its notification.</summary>
    ValueTask<bool> TryRemoveAsync(string stateName);

    /// <summary>Removes every state instance, disposes their runtimes and owned resources, then delivers their notifications.</summary>
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
/// Advanced SPI for custom registry implementations.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueStateRegistryNotificationDeferrer<T>
{
    /// <summary>Defers queued registry notifications until the returned scope is disposed.</summary>
    IConfiglueStateRegistryNotificationDeferral<T> DeferNotifications();
}

/// <summary>A scope that defers registry notifications and can cancel notifications for a runtime.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
/// <remarks>Disposal releases one deferral scope. Cancellation applies to queued events for the exact runtime instance.
/// Advanced SPI for custom registry implementations.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueStateRegistryNotificationDeferral<T> : IDisposable
{
    /// <summary>Cancels queued notifications associated with this exact runtime instance.</summary>
    void Cancel(IWritableState<T> runtime);
}
