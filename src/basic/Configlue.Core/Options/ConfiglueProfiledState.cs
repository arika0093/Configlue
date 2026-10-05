using System.Diagnostics;
using Configlue.CompilerServices;

namespace Configlue;

/// <summary>Manages profile state instances using a persisted catalog.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
/// <remarks>
/// Minimal concept: the catalog is ordinary persisted configuration holding a set of
/// profile names plus one active name. The runtime registry is only a materialization
/// cache: profile runtimes are created on demand and removal unloads the runtime while
/// leaving backing configuration data intact. Operations use ordinary optimistic
/// concurrency (a conditional catalog write; <see cref="StateConflictException"/> on a
/// lost race, safe to retry) and never coordinate catalog storage, registry events,
/// and user callbacks as one transaction. Registry listeners run synchronously on the
/// mutating caller's thread and must not synchronously wait for profile operations.
/// <see cref="ActiveProfileChanged"/> is best-effort: raised once after the operation
/// that changed the active profile completes, without ordering guarantees across
/// concurrent operations. Exceptions in listeners are logged and suppressed.
/// Loads fill the registry cache for catalog names (add-missing only, best-effort) so
/// profile instances keep composing with dynamic named states by
/// <c>(TModel, StateName)</c>. Unloaded or removed runtimes are never reaped here;
/// removal unloads on a best-effort basis and any missing runtime rematerializes
/// from retained backing data when requested.
/// </remarks>
internal sealed partial class ConfiglueProfiledState<TModel, TFragment>
    : IConfiglueProfiledState<TModel>,
        IDisposable,
        IAsyncDisposable
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly IConfiglueStateRegistry<TModel> _registry;
    private readonly StateSource<ConfiglueProfileCatalog> _catalogSource;
    private readonly string _defaultProfileName;
    private readonly IReadOnlyList<object> _ownedResources;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _subscriptionGate = new();
    private readonly HashSet<ActiveProfileValueSubscription> _subscriptions = [];
    private ConfiglueProfileCatalog? _catalog;
    private ConfiglueProfileCatalog? _ensuredCatalog;
    private Task? _disposeTask;
    private int _disposed;

    /// <summary>Creates a profile manager backed by the supplied catalog source.</summary>
    public ConfiglueProfiledState(
        IConfiglueStateRegistry<TModel> registry,
        StateSource<ConfiglueProfileCatalog> catalogSource,
        string defaultProfileName = "default",
        IReadOnlyList<object>? ownedResources = null
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
        _ownedResources = ownedResources ?? [];
    }

    /// <inheritdoc />
    public event Action<string>? ActiveProfileChanged;

    /// <inheritdoc />
    public string DefaultProfileName => _defaultProfileName;

    /// <inheritdoc />
    public IDisposable OnChange(Action<TModel> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var subscription = new ActiveProfileValueSubscription(this, listener);
        lock (_subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _subscriptions.Add(subscription);
        }

        try
        {
            subscription.Start();
            return subscription;
        }
        catch
        {
            subscription.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    // IDisposable is a synchronous cleanup boundary; prefer DisposeAsync when asynchronous resources are owned.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_subscriptionGate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            Volatile.Write(ref _disposed, 1);
            var subscriptions = _subscriptions.ToArray();
            disposeTask = DisposeCoreAsync(subscriptions);
            _disposeTask = disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyCollection<string>> GetProfileNamesAsync(
        CancellationToken cancellationToken = default
    )
    {
        var (catalog, notification) = await LoadUnderGateAsync(cancellationToken)
            .ConfigureAwait(false);
        NotifyAfterGate(notification);
        return Array.AsReadOnly(catalog.ProfileNames.ToArray());
    }

    /// <inheritdoc />
    public async ValueTask<string> GetActiveProfileNameAsync(
        CancellationToken cancellationToken = default
    )
    {
        var (catalog, notification) = await LoadUnderGateAsync(cancellationToken)
            .ConfigureAwait(false);
        NotifyAfterGate(notification);
        return catalog.ActiveProfileName!;
    }

    /// <inheritdoc />
    public async ValueTask<IWritableState<TModel>> GetProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileName(profileName);
        ConfiglueProfileCatalog catalog;
        string? notification;
        IWritableState<TModel> runtime;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (catalog, notification) = await LoadCatalogLockedAsync(cancellationToken)
                .ConfigureAwait(false);
            EnsureProfileExists(catalog, profileName);
            runtime = await MaterializeAsync(profileName).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        NotifyAfterGate(notification);
        return runtime;
    }

    /// <inheritdoc />
    public async ValueTask<IWritableState<TModel>> GetActiveProfileAsync(
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileName(_defaultProfileName);
        ConfiglueProfileCatalog catalog;
        string? notification;
        IWritableState<TModel> runtime;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (catalog, notification) = await LoadCatalogLockedAsync(cancellationToken)
                .ConfigureAwait(false);
            runtime = await MaterializeAsync(catalog.ActiveProfileName!).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        NotifyAfterGate(notification);
        return runtime;
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

        string? notification = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var addedRuntime = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var (catalog, _, externalNotification) = await ReadCatalogAsync(cancellationToken)
                .ConfigureAwait(false);
            notification = externalNotification;
            await EnsureRuntimesLockedAsync(catalog).ConfigureAwait(false);
            if (catalog.ProfileNames.Contains(profileName, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"The profile '{profileName}' already exists.");
            }

            TModel sourceValue = default!;
            var hasSourceValue = false;
            if (copyFrom is not null)
            {
                EnsureProfileExists(catalog, copyFrom);
                var sourceRuntime = await MaterializeAsync(copyFrom).ConfigureAwait(false);
                sourceValue = await sourceRuntime
                    .GetValueAsync(cancellationToken)
                    .ConfigureAwait(false);
                hasSourceValue = true;
            }

            var updated = Clone(catalog);
            updated.ProfileNames.Add(profileName);
            if (string.IsNullOrWhiteSpace(updated.ActiveProfileName))
            {
                updated.ActiveProfileName = profileName;
            }

            // An already-materialized dynamic state is adopted; a fixed StateName is a conflict.
            IWritableState<TModel> createdRuntime;
            if (_registry.TryGet(profileName, out var existing) && existing is not null)
            {
                createdRuntime = existing;
            }
            else
            {
                if (!await _registry.TryAddAsync(profileName).ConfigureAwait(false))
                {
                    if (_registry.TryGet(profileName, out existing) && existing is not null)
                    {
                        createdRuntime = existing;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"The profile name '{profileName}' conflicts with a fixed StateName."
                        );
                    }
                }
                else
                {
                    createdRuntime = _registry.Get(profileName);
                    addedRuntime = true;
                }
            }

            try
            {
                if (hasSourceValue)
                {
                    using var session = await AsAdvancedState(createdRuntime)
                        .OpenEditSessionAsync(cancellationToken)
                        .ConfigureAwait(false);
                    session.Value = sourceValue;
                    await session.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                // Profile values may share the catalog's physical resource and advance its
                // revision. Refresh the revision while the logical catalog still matches
                // before attempting the conditional write.
                var freshRevision = await RefreshRevisionAsync(catalog, cancellationToken)
                    .ConfigureAwait(false);
                await WriteCatalogAsync(updated, freshRevision, cancellationToken)
                    .ConfigureAwait(false);
                _catalog = updated;
                _ensuredCatalog = updated;
                if (ActiveChanged(catalog, updated))
                {
                    notification = updated.ActiveProfileName;
                }
            }
            catch
            {
                // The catalog is unchanged on failure; drop the optimistic cache so the
                // next operation rereads. A writer may report failure after committing,
                // so never trust the proposed catalog here.
                _catalog = null;
                if (addedRuntime)
                {
                    try
                    {
                        await _registry.TryRemoveAsync(profileName).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the original failure; cleanup is best-effort.
                    }
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
        NotifyAfterGate(notification);
    }

    /// <inheritdoc />
    public async ValueTask RemoveProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileName(profileName);
        string? notification = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var (catalog, revision, externalNotification) = await ReadCatalogAsync(
                    cancellationToken
                )
                .ConfigureAwait(false);
            notification = externalNotification;
            await EnsureRuntimesLockedAsync(catalog).ConfigureAwait(false);
            EnsureProfileExists(catalog, profileName);
            if (string.Equals(profileName, _defaultProfileName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The default profile cannot be removed.");
            }

            var updated = Clone(catalog);
            updated.ProfileNames.RemoveAll(name =>
                string.Equals(name, profileName, StringComparison.Ordinal)
            );
            if (string.Equals(updated.ActiveProfileName, profileName, StringComparison.Ordinal))
            {
                updated.ActiveProfileName = updated.ProfileNames[0];
            }

            try
            {
                await WriteCatalogAsync(updated, revision, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _catalog = null;
                throw;
            }
            _catalog = updated;
            _ensuredCatalog = updated;
            if (ActiveChanged(catalog, updated))
            {
                notification = updated.ActiveProfileName;
            }

            // Best-effort cache unload; backing configuration data is retained and the
            // runtime rematerializes on demand.
            try
            {
                await _registry.TryRemoveAsync(profileName).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue profile runtime unload failed: {0}", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
        NotifyAfterGate(notification);
    }

    /// <inheritdoc />
    public async ValueTask SetActiveProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileName(profileName);
        string? notification = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var (catalog, revision, externalNotification) = await ReadCatalogAsync(
                    cancellationToken
                )
                .ConfigureAwait(false);
            notification = externalNotification;
            await EnsureRuntimesLockedAsync(catalog).ConfigureAwait(false);
            EnsureProfileExists(catalog, profileName);
            if (string.Equals(profileName, catalog.ActiveProfileName, StringComparison.Ordinal))
            {
                _catalog = catalog;
                _ensuredCatalog = catalog;
                notification = null;
                return;
            }

            var updated = Clone(catalog);
            updated.ActiveProfileName = profileName;
            try
            {
                await WriteCatalogAsync(updated, revision, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _catalog = null;
                throw;
            }
            _catalog = updated;
            _ensuredCatalog = updated;
            notification = profileName;
        }
        finally
        {
            _gate.Release();
        }
        NotifyAfterGate(notification);
    }

    private void RemoveSubscription(ActiveProfileValueSubscription subscription)
    {
        lock (_subscriptionGate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private async Task<(ConfiglueProfileCatalog Catalog, string? Notification)> LoadUnderGateAsync(
        CancellationToken cancellationToken
    )
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var (catalog, _, notification) = await ReadCatalogAsync(cancellationToken)
                .ConfigureAwait(false);
            _catalog = catalog;
            await EnsureRuntimesLockedAsync(catalog).ConfigureAwait(false);
            return (catalog, notification);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(
        ConfiglueProfileCatalog Catalog,
        string? Notification
    )> LoadCatalogLockedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var (catalog, _, notification) = await ReadCatalogAsync(cancellationToken)
            .ConfigureAwait(false);
        _catalog = catalog;
        await EnsureRuntimesLockedAsync(catalog).ConfigureAwait(false);
        return (catalog, notification);
    }

    private async Task<(
        ConfiglueProfileCatalog Catalog,
        string? Revision,
        string? ActiveChangedNotification
    )> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        var previousActive = _catalog?.ActiveProfileName;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await _catalogSource
                .Reader.ReadAsync(ConfiglueResourceContext.Default, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status == StateReadStatus.Success)
            {
                if (result.Value is null)
                {
                    throw new InvalidDataException(
                        "The profile catalog source returned a null catalog."
                    );
                }

                var catalog = Normalize(result.Value, out var needsWrite);
                if (!needsWrite)
                {
                    return (catalog, result.Revision, ChangedNotification(previousActive, catalog));
                }

                try
                {
                    var writeResult = await _catalogSource
                        .Writer!.WriteAsync(
                            ConfiglueResourceContext.Default,
                            new StateWriteRequest<ConfiglueProfileCatalog>(
                                catalog,
                                Condition: RevisionCondition.FromRevision(result.Revision)
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return (
                        catalog,
                        writeResult.Revision,
                        ChangedNotification(previousActive, catalog)
                    );
                }
                catch (StateConflictException) when (attempt < 4)
                {
                    continue;
                }
            }
            else if (result.Status == StateReadStatus.NotFound)
            {
                var catalog = CreateDefaultCatalog();
                try
                {
                    var writeResult = await _catalogSource
                        .Writer!.WriteAsync(
                            ConfiglueResourceContext.Default,
                            new StateWriteRequest<ConfiglueProfileCatalog>(
                                catalog,
                                Condition: RevisionCondition.FromRevision(result.Revision)
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return (
                        catalog,
                        writeResult.Revision,
                        ChangedNotification(previousActive, catalog)
                    );
                }
                catch (StateConflictException) when (attempt < 4)
                {
                    continue;
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"The profile catalog could not be read: {result.Status}."
                );
            }
        }

        throw new StateConflictException(
            "The profile catalog changed repeatedly while it was being read."
        );
    }

    private async Task<string?> RefreshRevisionAsync(
        ConfiglueProfileCatalog baseCatalog,
        CancellationToken cancellationToken
    )
    {
        var current = await _catalogSource
            .Reader.ReadAsync(ConfiglueResourceContext.Default, cancellationToken)
            .ConfigureAwait(false);
        if (current.Status != StateReadStatus.Success || current.Value is null)
        {
            _catalog = null;
            throw new StateConflictException(
                "The profile catalog changed before it could be updated."
            );
        }

        var normalized = Normalize(current.Value, out _);
        if (!CatalogEquals(normalized, baseCatalog))
        {
            _catalog = normalized;
            throw new StateConflictException("The profile catalog was updated by another process.");
        }

        return current.Revision;
    }

    private static bool CatalogEquals(
        ConfiglueProfileCatalog left,
        ConfiglueProfileCatalog right
    ) =>
        left.ProfileNames.SequenceEqual(right.ProfileNames, StringComparer.Ordinal)
        && string.Equals(left.ActiveProfileName, right.ActiveProfileName, StringComparison.Ordinal);

    private async Task WriteCatalogAsync(
        ConfiglueProfileCatalog catalog,
        string? revision,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _catalogSource
                .Writer!.WriteAsync(
                    ConfiglueResourceContext.Default,
                    new StateWriteRequest<ConfiglueProfileCatalog>(
                        catalog,
                        Condition: RevisionCondition.FromRevision(revision)
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (StateConflictException)
        {
            _catalog = null;
            throw new StateConflictException("The profile catalog was updated by another process.");
        }
        catch
        {
            // A writer may report failure after committing; force a reread next time.
            _catalog = null;
            throw;
        }
    }

    private static string? ChangedNotification(
        string? previousActive,
        ConfiglueProfileCatalog catalog
    ) =>
        previousActive is not null
        && !string.Equals(previousActive, catalog.ActiveProfileName, StringComparison.Ordinal)
            ? catalog.ActiveProfileName
            : null;

    private static bool ActiveChanged(
        ConfiglueProfileCatalog before,
        ConfiglueProfileCatalog after
    ) =>
        !string.Equals(before.ActiveProfileName, after.ActiveProfileName, StringComparison.Ordinal);

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

    private static void EnsureProfileExists(ConfiglueProfileCatalog catalog, string profileName)
    {
        if (!catalog.ProfileNames.Contains(profileName, StringComparer.Ordinal))
        {
            throw new KeyNotFoundException($"The profile '{profileName}' does not exist.");
        }
    }

    // The registry is a materialization cache; the catalog is the source of truth.
    // A missing runtime is recreated on demand from retained backing data.
    // Loads additionally fill the cache for every catalog name (add-missing only,
    // best-effort) so reopened contexts keep resolving profiles by (TModel, StateName).
    // Entries are never reaped here: explicit removal unloads on a best-effort basis.
    private async Task EnsureRuntimesLockedAsync(ConfiglueProfileCatalog catalog)
    {
        if (_ensuredCatalog is not null && CatalogEquals(_ensuredCatalog, catalog))
        {
            return;
        }

        foreach (var profileName in catalog.ProfileNames)
        {
            if (!_registry.TryGet(profileName, out _))
            {
                await _registry.TryAddAsync(profileName).ConfigureAwait(false);
            }
        }

        _ensuredCatalog = catalog;
    }

    private async ValueTask<IWritableState<TModel>> MaterializeAsync(string profileName)
    {
        if (_registry.TryGet(profileName, out var existing) && existing is not null)
        {
            return existing;
        }

        if (await _registry.TryAddAsync(profileName).ConfigureAwait(false))
        {
            return _registry.Get(profileName);
        }

        if (_registry.TryGet(profileName, out existing) && existing is not null)
        {
            return existing;
        }

        throw new InvalidOperationException(
            $"The profile name '{profileName}' conflicts with a fixed StateName."
        );
    }

    private static ConfiglueProfileCatalog Clone(ConfiglueProfileCatalog source) =>
        new()
        {
            ProfileNames = source.ProfileNames.ToList(),
            ActiveProfileName = source.ActiveProfileName,
        };

    private static IConfiglueEditSessions<TModel> AsAdvancedState(IWritableState<TModel> state) =>
        state as IConfiglueEditSessions<TModel>
        ?? throw new InvalidOperationException(
            "The profile registry returned a state without edit-session support."
        );

    private static void ValidateProfileName(string profileName)
    {
        // Profiles share the logical state-name namespace; only empty or whitespace names are invalid.
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
    }

    private void NotifyAfterGate(string? profileName)
    {
        if (profileName is null)
        {
            return;
        }

        NotifyActiveProfileChanged(profileName);
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

    private async Task DisposeCoreAsync(ActiveProfileValueSubscription[] subscriptions)
    {
        // Leave the owner's subscription lock before cancelling sources or awaiting work.
        await Task.Yield();
        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
        await Task.WhenAll(
                subscriptions.Select(static subscription => subscription.WaitForCompletionAsync())
            )
            .ConfigureAwait(false);

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Release();

        foreach (var resource in _ownedResources)
        {
            await ConfiglueOwnedResources.DisposeAsync(resource).ConfigureAwait(false);
        }
    }
}
