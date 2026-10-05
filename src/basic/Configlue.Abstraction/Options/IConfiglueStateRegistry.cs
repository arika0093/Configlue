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
/// <para>
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
/// </para>
/// <para>
/// Compatibility notes for the unified registry (formerly
/// <c>ConfiglueStateRegistry</c> and <c>ConfiglueFacadeStateRegistry</c>):
/// <list type="bullet">
/// <item>Concurrent-operation aggregation is gone. <c>ClearAsync</c>/<c>DisposeAsync</c>
/// no longer wait for in-flight <c>TryRemoveAsync</c> operations and no longer aggregate
/// their errors. Each operation disposes and reports only the entries it retired; a
/// concurrent removal failure is observed only by that removal. An empty
/// <c>ClearAsync</c> completes successfully and never surfaces another operation's
/// pending failure.</item>
/// <item>Cleanup <c>AggregateException</c> messages changed. Clearing now throws
/// with base message <c>"One or more Configlue states failed to clear."</c> and disposal
/// with base message <c>"One or more Configlue states failed to dispose."</c>, replacing the
/// former <c>"One or more Configlue runtimes failed to dispose."</c> and
/// <c>"One or more dynamic Configlue state failed to clear."</c> /
/// <c>"One or more dynamic Configlue state failed to dispose."</c> strings. Do not
/// match on the old messages.</item>
/// <item>Deferred completion is disposal completion, not notification completion. While
/// notifications are deferred, <c>TryAddAsync</c>/<c>TryRemoveAsync</c>/
/// <c>ClearAsync</c>/<c>DisposeAsync</c> complete after the mutation and runtime disposal
/// and before the queued callbacks run. In particular, a deferred <c>DisposeAsync</c>
/// task completes once entries are retired and disposed; <c>StateRemoved</c> delivery
/// still waits for the last deferral scope to be disposed.</item>
/// <item><c>StateNames</c> now always throws <c>ObjectDisposedException</c> once disposal
/// has started. The former core registry returned a snapshot after disposal while the
/// facade registry threw; the unified contract follows the throwing behavior.</item>
/// </list>
/// </para>
/// <para>
/// Advanced application API for dynamic named states.
/// </para>
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
    /// <remarks>Delivers its remove notification before completing unless notifications are deferred,
    /// in which case disposal completes first and delivery waits for the deferral scope.
    /// Does not observe concurrent removals; only this entry's disposal failure is reported.</remarks>
    ValueTask<bool> TryRemoveAsync(string stateName);

    /// <summary>Removes every state instance, disposes their runtimes and owned resources, then delivers their notifications.</summary>
    /// <remarks>Delivers its remove notifications before completing unless notifications are deferred,
    /// in which case disposal completes first and delivery waits for the deferral scope.
    /// Does not wait for or aggregate concurrent in-flight removals; only entries retired by
    /// this call are disposed and reported. Throws <c>AggregateException</c> with base message
    /// <c>"One or more Configlue states failed to clear."</c> when its own disposals fail.</remarks>
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
