namespace Configlue;

/// <summary>
/// Manages Configlue profiles as persisted, catalog-managed named state instances.
/// </summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <remarks>
/// <para>
/// A profile is a named state instance whose membership and active selection are persisted in a
/// <see cref="ConfiglueProfileCatalog"/>. Its logical identity is <c>(TModel, StateName)</c> and it
/// shares one logical state-name namespace with fixed states and dynamic named states. A profile
/// therefore cannot be created when a fixed state with the same <c>(TModel, StateName)</c>
/// identity already exists, but creating a profile never reserves a name beyond that identity.
/// </para>
/// <para>
/// The profile catalog is ordinary persisted configuration holding the set of names plus one
/// active name. The dynamic-state registry is only a materialization cache: profile runtimes
/// are created on demand, unloading a runtime never removes catalog membership, and removal
/// unloads the runtime while leaving backing configuration data available for later
/// materialization. Operations use ordinary optimistic concurrency: a lost race throws
/// <c>StateConflictException</c> without corrupting committed data and is safe to retry.
/// Catalog writes, registry events, and user callbacks are not coordinated as one
/// transaction. <c>ActiveProfileChanged</c> is best-effort (raised after the changing
/// operation completes, without ordering guarantees across concurrent operations), and
/// registry listeners must not synchronously wait for profile operations.
/// </para>
/// </remarks>
/// <remarks>Advanced application API for profiled state.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueProfiledState<TModel>
{
    /// <summary>The configured profile name that cannot be removed.</summary>
    string DefaultProfileName { get; }

    /// <summary>Raised after the active profile changes (best-effort, unordered).</summary>
    event Action<string>? ActiveProfileChanged;

    /// <summary>Subscribes to changes in the active profile value and active-profile selection.</summary>
    /// <remarks>Binding starts asynchronously and does not emit an initial value. Profile switches asynchronously read the selected value; a newer selection or value notification supersedes pending reads. Dispose detaches listeners and cancels pending reads. Read failures are logged. Read the initial value with GetActiveValueAsync when needed.</remarks>
    IDisposable OnChange(Action<TModel> listener);

    /// <summary>Gets the persisted profile names in display order.</summary>
    ValueTask<IReadOnlyCollection<string>> GetProfileNamesAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>Gets the name of the active profile.</summary>
    ValueTask<string> GetActiveProfileNameAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets a writable profile state instance by name, materializing its runtime from the catalog when necessary.</summary>
    ValueTask<IWritableState<TModel>> GetProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    );

    /// <summary>Gets the writable state instance for the active profile.</summary>
    ValueTask<IWritableState<TModel>> GetActiveProfileAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>Reads the active profile's current value.</summary>
    ValueTask<TModel> GetActiveValueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates and persists a profile, optionally copying another profile's current value.
    /// </summary>
    /// <remarks>
    /// An already-materialized dynamic named state with the same logical identity is adopted into
    /// the catalog. A fixed state with the same <c>(TModel, StateName)</c> identity is a conflict.
    /// </remarks>
    ValueTask CreateProfileAsync(
        string profileName,
        string? copyFrom = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Removes a profile's catalog membership and unloads its runtime on a best-effort basis. The backing configuration data is retained; the default cannot be removed.</summary>
    ValueTask RemoveProfileAsync(string profileName, CancellationToken cancellationToken = default);

    /// <summary>Persists a new active profile selection.</summary>
    ValueTask SetActiveProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    );
}
