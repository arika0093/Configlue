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
/// The profile catalog is the source of truth for which named states are profiles and which is
/// active. The dynamic-state registry is only a materialization cache; unloading a runtime from
/// the registry does not remove the persisted profile, and reading a profile rematerializes its
/// runtime when needed. Removing a profile removes its catalog membership and may unload the
/// runtime, but leaves the backing configuration data available for later materialization.
/// </para>
/// </remarks>
public interface IConfiglueProfiledState<TModel>
{
    /// <summary>The configured profile name that cannot be removed.</summary>
    string DefaultProfileName { get; }

    /// <summary>Raised after the active profile changes.</summary>
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

    /// <summary>Removes a profile's catalog membership and may unload its runtime. The backing configuration data is retained; the default cannot be removed.</summary>
    ValueTask RemoveProfileAsync(string profileName, CancellationToken cancellationToken = default);

    /// <summary>Persists a new active profile selection.</summary>
    ValueTask SetActiveProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    );
}
