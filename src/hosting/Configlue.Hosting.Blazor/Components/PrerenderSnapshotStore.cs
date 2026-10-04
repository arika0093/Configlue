using Microsoft.AspNetCore.Components;

namespace Configlue.Hosting.Blazor;

/// <summary>
/// Bridges <see cref="StateReader{T}"/> prerender handoff across render modes.
/// </summary>
/// <remarks>
/// Browser storage cannot be read during server prerender, so the prerendered value is handed
/// to the interactive renderer through this store. Production resolves the store from
/// <see cref="PersistentComponentState"/>; tests register a fake.
/// <para>
/// Registration and restore are unconditional: registering a persist callback outside prerender
/// is harmless (it only runs while the renderer persists state), and restore is a no-op when
/// nothing was persisted.
/// </para>
/// </remarks>
internal interface IPrerenderSnapshotStore
{
    /// <summary>Takes a previously persisted value, removing it so it is restored at most once.</summary>
    bool TryTake<TValue>(string key, out TValue? value);

    /// <summary>Persists a value for the interactive renderer.</summary>
    void Persist<TValue>(string key, TValue value);

    /// <summary>Registers a callback invoked when prerender state is persisted.</summary>
    IDisposable OnPersisting(Func<Task> callback);
}

/// <summary>Adapts Blazor <see cref="PersistentComponentState"/> to <see cref="IPrerenderSnapshotStore"/>.</summary>
internal sealed class PersistentComponentStateStore : IPrerenderSnapshotStore
{
    private readonly PersistentComponentState _state;

    internal PersistentComponentStateStore(PersistentComponentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public bool TryTake<TValue>(string key, out TValue? value) =>
        _state.TryTakeFromJson<TValue>(key, out value);

    /// <inheritdoc />
    public void Persist<TValue>(string key, TValue value) => _state.PersistAsJson(key, value);

    /// <inheritdoc />
    public IDisposable OnPersisting(Func<Task> callback) => _state.RegisterOnPersisting(callback);
}
