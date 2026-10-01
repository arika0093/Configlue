namespace Configlue.Extensions.Blazor;

/// <summary>Read-only view over a component-resolved configuration snapshot.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public sealed class StateReaderContext<T>
{
    private readonly StateReader<T> _owner;

    internal StateReaderContext(StateReader<T> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <summary>The most recently resolved snapshot, or null before the first successful load.</summary>
    public StateSnapshot<T>? Snapshot => _owner.Snapshot;

    /// <summary>
    /// The resolved effective value. Falls back to the default value while the first snapshot loads.
    /// </summary>
    public T Value => _owner.Snapshot is null ? default! : _owner.Snapshot.Value;

    /// <summary>Whether the initial snapshot is still loading.</summary>
    public bool IsLoading => _owner.IsLoading;

    /// <summary>Whether a snapshot has been successfully resolved at least once.</summary>
    public bool HasValue => _owner.Snapshot is not null;

    /// <summary>The failure that prevented the initial snapshot from loading.</summary>
    public Exception? LoadFailure => _owner.LoadFailure;

    /// <summary>
    /// The failure that prevented the most recent reload. The last successfully rendered value is retained.
    /// </summary>
    public Exception? ReloadFailure => _owner.ReloadFailure;
}
