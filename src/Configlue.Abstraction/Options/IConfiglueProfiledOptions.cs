namespace Configlue;

/// <summary>Manages a persistent catalog of named Configlue options profiles.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
public interface IConfiglueProfiledOptions<TModel>
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

    /// <summary>Gets a writable profile by name.</summary>
    ValueTask<IWritableOptions<TModel>> GetProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    );

    /// <summary>Gets the writable options for the active profile.</summary>
    ValueTask<IWritableOptions<TModel>> GetActiveProfileAsync(
        CancellationToken cancellationToken = default
    );

    /// <summary>Reads the active profile's current value.</summary>
    ValueTask<TModel> GetActiveValueAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates and persists a profile, optionally copying another profile's current value.</summary>
    ValueTask CreateProfileAsync(
        string profileName,
        string? copyFrom = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Removes a profile from the catalog and runtime. Its backing state is retained; the default cannot be removed.</summary>
    ValueTask RemoveProfileAsync(string profileName, CancellationToken cancellationToken = default);

    /// <summary>Persists a new active profile selection.</summary>
    ValueTask SetActiveProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    );
}
