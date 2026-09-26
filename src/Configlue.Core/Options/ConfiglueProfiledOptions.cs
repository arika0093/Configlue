using System.Diagnostics;

namespace Configlue;

/// <summary>Manages profile options instances using a persisted catalog.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
public sealed class ConfiglueProfiledOptions<TModel, TFragment> : IConfiglueProfiledOptions<TModel>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        nameof(ConfiglueProfileCatalog.ActiveProfileName),
        nameof(ConfiglueProfileCatalog.ProfileNames),
    };

    private readonly IConfiglueOptionsRegistry<TModel> _registry;
    private readonly StateSource<ConfiglueProfileCatalog> _catalogSource;
    private readonly string _defaultProfileName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConfiglueProfileCatalog? _catalog;
    private bool _initialized;

    /// <summary>Creates a profile manager backed by the supplied catalog source.</summary>
    public ConfiglueProfiledOptions(
        IConfiglueOptionsRegistry<TModel> registry,
        StateSource<ConfiglueProfileCatalog> catalogSource,
        string defaultProfileName = "default"
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(catalogSource);
        ValidateProfileName(defaultProfileName);
        if (catalogSource.Writer is null)
        {
            throw new ArgumentException(
                "The profile catalog source must support writes.",
                nameof(catalogSource)
            );
        }

        _registry = registry;
        _catalogSource = catalogSource;
        _defaultProfileName = defaultProfileName;
    }

    /// <inheritdoc />
    public event Action<string>? ActiveProfileChanged;

    /// <inheritdoc />
    public string DefaultProfileName => _defaultProfileName;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyCollection<string>> GetProfileNamesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return Array.AsReadOnly(_catalog!.ProfileNames.ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<string> GetActiveProfileNameAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return _catalog!.ActiveProfileName!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<IWritableOptions<TModel>> GetProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileName(profileName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            EnsureProfileExists(profileName);
            return _registry.Get(profileName);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<IWritableOptions<TModel>> GetActiveProfileAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return _registry.Get(_catalog!.ActiveProfileName!);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<TModel> GetActiveValueAsync(
        CancellationToken cancellationToken = default
    )
    {
        var activeProfile = await GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        return await activeProfile.GetValueAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CreateProfileAsync(
        string profileName,
        string? copyFrom = null,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileName(profileName);
        if (copyFrom is not null)
        {
            ValidateProfileName(copyFrom);
        }

        string? activeProfileChanged = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            if (_catalog!.ProfileNames.Contains(profileName, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"The profile '{profileName}' already exists.");
            }

            TModel sourceValue = default!;
            var hasSourceValue = false;
            if (copyFrom is not null)
            {
                EnsureProfileExists(copyFrom);
                sourceValue = await _registry
                    .Get(copyFrom)
                    .GetValueAsync(cancellationToken)
                    .ConfigureAwait(false);
                hasSourceValue = true;
            }

            var updated = Clone(_catalog);
            updated.ProfileNames.Add(profileName);
            if (string.IsNullOrWhiteSpace(updated.ActiveProfileName))
            {
                updated.ActiveProfileName = profileName;
                activeProfileChanged = profileName;
            }

            await PersistCatalogAsync(updated, cancellationToken).ConfigureAwait(false);
            if (!_registry.TryAdd(profileName) && !_registry.TryGet(profileName, out _))
            {
                throw new InvalidOperationException(
                    $"The profile '{profileName}' is already registered at runtime."
                );
            }

            if (hasSourceValue)
            {
                await _registry
                    .Get(profileName)
                    .SaveAsync(sourceValue, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (activeProfileChanged is not null)
        {
            NotifyActiveProfileChanged(activeProfileChanged);
        }
    }

    /// <inheritdoc />
    public async ValueTask RemoveProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileName(profileName);
        string? activeProfileChanged = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            EnsureProfileExists(profileName);
            if (string.Equals(profileName, _defaultProfileName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The default profile cannot be removed.");
            }

            var updated = Clone(_catalog!);
            updated.ProfileNames.RemoveAll(name =>
                string.Equals(name, profileName, StringComparison.Ordinal)
            );
            if (string.Equals(updated.ActiveProfileName, profileName, StringComparison.Ordinal))
            {
                updated.ActiveProfileName = updated.ProfileNames[0];
                activeProfileChanged = updated.ActiveProfileName;
            }

            await PersistCatalogAsync(updated, cancellationToken).ConfigureAwait(false);
            _registry.TryRemove(profileName);
        }
        finally
        {
            _gate.Release();
        }

        if (activeProfileChanged is not null)
        {
            NotifyActiveProfileChanged(activeProfileChanged);
        }
    }

    /// <inheritdoc />
    public async ValueTask SetActiveProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileName(profileName);
        string? activeProfileChanged = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            EnsureProfileExists(profileName);
            if (string.Equals(profileName, _catalog!.ActiveProfileName, StringComparison.Ordinal))
            {
                return;
            }

            var updated = Clone(_catalog);
            updated.ActiveProfileName = profileName;
            await PersistCatalogAsync(updated, cancellationToken).ConfigureAwait(false);
            activeProfileChanged = profileName;
        }
        finally
        {
            _gate.Release();
        }

        if (activeProfileChanged is not null)
        {
            NotifyActiveProfileChanged(activeProfileChanged);
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await _catalogSource
                .Reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            ConfiglueProfileCatalog catalog;
            bool needsWrite;
            if (result.Status == StateReadStatus.Success)
            {
                if (result.Value is null)
                {
                    throw new InvalidDataException(
                        "The profile catalog source returned a null catalog."
                    );
                }

                catalog = Normalize(result.Value, out needsWrite);
            }
            else if (result.Status == StateReadStatus.NotFound)
            {
                catalog = CreateDefaultCatalog();
                needsWrite = true;
            }
            else
            {
                throw new InvalidOperationException(
                    $"The profile catalog could not be read: {result.Status}."
                );
            }

            if (needsWrite)
            {
                try
                {
                    await _catalogSource
                        .Writer!.WriteAsync(
                            new StateWriteRequest<ConfiglueProfileCatalog>(
                                catalog,
                                result.Revision,
                                CheckRevision: true
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                catch (StateConflictException) when (attempt < 4)
                {
                    continue;
                }
            }

            _catalog = catalog;
            await SynchronizeRegistryAsync(catalog).ConfigureAwait(false);
            _initialized = true;
            return;
        }

        throw new StateConflictException(
            "The profile catalog changed repeatedly during initialization."
        );
    }

    private async Task PersistCatalogAsync(
        ConfiglueProfileCatalog catalog,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var expectedCatalog = _catalog!;
            var current = await _catalogSource
                .Reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current.Status != StateReadStatus.Success || current.Value is null)
            {
                await RefreshCatalogAfterConflictAsync(cancellationToken).ConfigureAwait(false);
                throw new StateConflictException(
                    "The profile catalog changed before it could be updated."
                );
            }

            var currentCatalog = Normalize(current.Value, out _);
            if (!CatalogEquals(currentCatalog, expectedCatalog))
            {
                await RefreshCatalogAfterConflictAsync(cancellationToken).ConfigureAwait(false);
                throw new StateConflictException(
                    "The profile catalog was updated by another process."
                );
            }

            // Profile values can share the catalog's physical resource and advance its resource revision.
            // Refresh the token while the logical catalog still matches before attempting a conditional write.
            try
            {
                await _catalogSource
                    .Writer!.WriteAsync(
                        new StateWriteRequest<ConfiglueProfileCatalog>(
                            catalog,
                            current.Revision,
                            CheckRevision: true
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                _catalog = catalog;
                return;
            }
            catch (StateConflictException)
            {
                await RefreshCatalogAfterConflictAsync(cancellationToken).ConfigureAwait(false);
                if (attempt < 2 && CatalogEquals(_catalog!, expectedCatalog))
                {
                    continue;
                }

                throw;
            }
        }

        throw new StateConflictException(
            "The profile catalog changed repeatedly while it was being updated."
        );
    }

    private async Task RefreshCatalogAfterConflictAsync(CancellationToken cancellationToken)
    {
        _initialized = false;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SynchronizeRegistryAsync(ConfiglueProfileCatalog catalog)
    {
        var expected = new HashSet<string>(catalog.ProfileNames, StringComparer.Ordinal);
        foreach (var profileName in catalog.ProfileNames)
        {
            if (!_registry.TryGet(profileName, out _))
            {
                _registry.TryAdd(profileName);
            }
        }

        foreach (
            var registeredName in _registry.ProfileNames.Where(name => !expected.Contains(name))
        )
        {
            _registry.TryRemove(registeredName);
        }
    }

    private ConfiglueProfileCatalog Normalize(ConfiglueProfileCatalog source, out bool changed)
    {
        var originalNames = source.ProfileNames;
        var names = originalNames?.ToList() ?? [];
        foreach (var profileName in names)
        {
            ValidateProfileName(profileName);
        }

        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            throw new InvalidDataException("The profile catalog contains duplicate names.");
        }

        if (!names.Contains(_defaultProfileName, StringComparer.Ordinal))
        {
            names.Insert(0, _defaultProfileName);
        }

        var activeProfileName = source.ActiveProfileName;
        if (
            string.IsNullOrWhiteSpace(activeProfileName)
            || !names.Contains(activeProfileName, StringComparer.Ordinal)
        )
        {
            activeProfileName = _defaultProfileName;
        }

        changed =
            originalNames is null
            || !originalNames.SequenceEqual(names, StringComparer.Ordinal)
            || !string.Equals(
                source.ActiveProfileName,
                activeProfileName,
                StringComparison.Ordinal
            );
        return new ConfiglueProfileCatalog
        {
            ProfileNames = names,
            ActiveProfileName = activeProfileName,
        };
    }

    private ConfiglueProfileCatalog CreateDefaultCatalog() =>
        new() { ProfileNames = [_defaultProfileName], ActiveProfileName = _defaultProfileName };

    private void EnsureProfileExists(string profileName)
    {
        if (!_catalog!.ProfileNames.Contains(profileName, StringComparer.Ordinal))
        {
            throw new KeyNotFoundException($"The profile '{profileName}' does not exist.");
        }
    }

    private static ConfiglueProfileCatalog Clone(ConfiglueProfileCatalog source) =>
        new()
        {
            ProfileNames = source.ProfileNames.ToList(),
            ActiveProfileName = source.ActiveProfileName,
        };

    private static bool CatalogEquals(
        ConfiglueProfileCatalog left,
        ConfiglueProfileCatalog right
    ) =>
        left.ProfileNames.SequenceEqual(right.ProfileNames, StringComparer.Ordinal)
        && string.Equals(left.ActiveProfileName, right.ActiveProfileName, StringComparison.Ordinal);

    private static void ValidateProfileName(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        if (
            profileName.Contains(':')
            || profileName.Contains("__", StringComparison.Ordinal)
            || ReservedNames.Contains(profileName)
        )
        {
            throw new ArgumentException(
                $"'{profileName}' is not a valid profile name.",
                nameof(profileName)
            );
        }
    }

    private void NotifyActiveProfileChanged(string profileName)
    {
        var handlers = ActiveProfileChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(profileName);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue active-profile listener failed: {0}", exception);
            }
        }
    }
}
