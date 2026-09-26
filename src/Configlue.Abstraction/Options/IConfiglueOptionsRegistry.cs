namespace Configlue;

/// <summary>Manages named options profiles that can be added and removed at runtime.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IConfiglueOptionsRegistry<T> : IDisposable
{
    /// <summary>The currently registered profile names.</summary>
    IReadOnlyCollection<string> ProfileNames { get; }

    /// <summary>Gets a registered profile or throws when the name is unknown.</summary>
    IWritableOptions<T> Get(string profileName);

    /// <summary>Tries to retrieve a registered profile.</summary>
    bool TryGet(string profileName, out IWritableOptions<T>? options);

    /// <summary>Creates and registers a profile if its name is not already in use.</summary>
    bool TryAdd(string profileName);

    /// <summary>Removes a profile and disposes its runtime when it is disposable.</summary>
    bool TryRemove(string profileName);

    /// <summary>Removes every registered profile.</summary>
    void Clear();

    /// <summary>Raised after a profile is registered.</summary>
    event Action<string, IWritableOptions<T>>? ProfileAdded;

    /// <summary>Raised after a profile is removed.</summary>
    event Action<string>? ProfileRemoved;
}
