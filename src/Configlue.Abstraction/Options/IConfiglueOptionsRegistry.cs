namespace Configlue;

/// <summary>Manages named options profiles that can be added and removed at runtime.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IConfiglueOptionsRegistry<T> : IDisposable, IAsyncDisposable
{
    /// <summary>The currently registered profile names.</summary>
    IReadOnlyCollection<string> ProfileNames { get; }

    /// <summary>Gets a registered profile or throws when the name is unknown.</summary>
    IWritableOptions<T> Get(string profileName);

    /// <summary>Tries to retrieve a registered profile.</summary>
    bool TryGet(string profileName, out IWritableOptions<T>? options);

    /// <summary>Creates and registers a profile if its name is not already in use.</summary>
    bool TryAdd(string profileName);

    /// <summary>Removes a profile and synchronously waits for its runtime and watchers to stop.</summary>
    bool TryRemove(string profileName);

    /// <summary>
    /// Removes a profile and waits for its runtime and watchers to stop.
    /// Implementations that do not provide asynchronous cleanup use the synchronous removal path.
    /// </summary>
    ValueTask<bool> TryRemoveAsync(string profileName) =>
        ValueTask.FromResult(TryRemove(profileName));

    /// <summary>Removes every registered profile and synchronously waits for their runtimes and watchers to stop.</summary>
    void Clear();

    /// <summary>
    /// Removes every profile and waits for their runtimes and watchers to stop.
    /// Implementations that do not provide asynchronous cleanup use the synchronous clear path.
    /// </summary>
    ValueTask ClearAsync()
    {
        Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>Uses synchronous disposal when an implementation does not provide async cleanup.</summary>
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Raised after a profile is registered.</summary>
    event Action<string, IWritableOptions<T>>? ProfileAdded;

    /// <summary>Raised after a profile is removed.</summary>
    event Action<string>? ProfileRemoved;
}

/// <summary>Allows a registry to defer notifications while a profile-manager operation is in progress.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
/// <remarks>
/// Implement this capability when registry event listeners can reenter a profile manager that
/// uses the registry. The manager disposes the returned scope after releasing its own operation
/// gate, allowing queued callbacks to query the manager without deadlocking. Implementations
/// should preserve FIFO order and support nested scopes; notifications resume when the last scope
/// is disposed.
/// </remarks>
public interface IConfiglueOptionsRegistryNotificationDeferrer<T>
{
    /// <summary>Defers queued registry notifications until the returned scope is disposed.</summary>
    IConfiglueOptionsRegistryNotificationDeferral<T> DeferNotifications();
}

/// <summary>A scope that defers registry notifications and can cancel notifications for a runtime.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
/// <remarks>Disposal releases one deferral scope. Cancellation applies to queued events for the exact runtime instance.</remarks>
public interface IConfiglueOptionsRegistryNotificationDeferral<T> : IDisposable
{
    /// <summary>Cancels queued notifications associated with this exact runtime instance.</summary>
    void Cancel(IWritableOptions<T> runtime);
}
