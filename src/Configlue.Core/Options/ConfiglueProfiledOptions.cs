using System.Diagnostics;

namespace Configlue;

/// <summary>Manages profile options instances using a persisted catalog.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
public sealed class ConfiglueProfiledOptions<TModel, TFragment>
    : IConfiglueProfiledOptions<TModel>,
        IDisposable,
        IAsyncDisposable
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
    private readonly HashSet<string> _catalogRuntimeNames = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _activeProfileNotificationGate = new();
    private readonly object _subscriptionGate = new();
    private readonly Queue<string> _pendingActiveProfileNotifications = new();
    private readonly HashSet<ActiveProfileValueSubscription> _subscriptions = [];
    private readonly CancellationTokenSource _watcherCancellation = new();
    private ConfiglueProfileCatalog? _catalog;
    private string? _catalogRevision;
    private Task? _catalogWatchTask;
    private Task? _disposeTask;
    private int _disposed;
    private bool _dispatchingActiveProfileNotifications;
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
    public TModel CurrentValue =>
        GetActiveValueAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

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
            _watcherCancellation.Cancel();
            foreach (var subscription in _subscriptions.ToArray())
            {
                subscription.Dispose();
            }
            _subscriptions.Clear();
            disposeTask = DisposeCoreAsync(_catalogWatchTask);
            _disposeTask = disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyCollection<string>> GetProfileNamesAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IConfiglueOptionsRegistryNotificationDeferral<TModel>? notificationScope = null;
        try
        {
            notificationScope = DeferRegistryNotifications();
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return Array.AsReadOnly(_catalog!.ProfileNames.ToArray());
        }
        finally
        {
            _gate.Release();
            try
            {
                notificationScope?.Dispose();
            }
            finally
            {
                DrainPendingActiveProfileNotifications();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<string> GetActiveProfileNameAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IConfiglueOptionsRegistryNotificationDeferral<TModel>? notificationScope = null;
        try
        {
            notificationScope = DeferRegistryNotifications();
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return _catalog!.ActiveProfileName!;
        }
        finally
        {
            _gate.Release();
            try
            {
                notificationScope?.Dispose();
            }
            finally
            {
                DrainPendingActiveProfileNotifications();
            }
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
        IConfiglueOptionsRegistryNotificationDeferral<TModel>? notificationScope = null;
        try
        {
            notificationScope = DeferRegistryNotifications();
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            EnsureProfileExists(profileName);
            return _registry.Get(profileName);
        }
        finally
        {
            _gate.Release();
            try
            {
                notificationScope?.Dispose();
            }
            finally
            {
                DrainPendingActiveProfileNotifications();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<IWritableOptions<TModel>> GetActiveProfileAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IConfiglueOptionsRegistryNotificationDeferral<TModel>? notificationScope = null;
        try
        {
            notificationScope = DeferRegistryNotifications();
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return _registry.Get(_catalog!.ActiveProfileName!);
        }
        finally
        {
            _gate.Release();
            try
            {
                notificationScope?.Dispose();
            }
            finally
            {
                DrainPendingActiveProfileNotifications();
            }
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
    public async ValueTask<StateWriteResult> SaveAsync(
        TModel value,
        CancellationToken cancellationToken = default
    )
    {
        var activeProfile = await GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        using var session = await activeProfile
            .OpenEditSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        session.Value = value;
        return await session.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(
        Action<TModel> update,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(update);
        var activeProfile = await GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        using var session = await activeProfile
            .OpenEditSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        update(session.Value);
        return await session.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(
        Func<TModel, Task> update,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(update);
        var activeProfile = await GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        using var session = await activeProfile
            .OpenEditSessionAsync(cancellationToken)
            .ConfigureAwait(false);
        await update(session.Value).ConfigureAwait(false);
        return await session.CommitAsync(cancellationToken).ConfigureAwait(false);
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IConfiglueOptionsRegistryNotificationDeferral<TModel>? notificationScope = null;
        try
        {
            notificationScope = DeferRegistryNotifications();
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

            var originalActiveProfileName = _catalog.ActiveProfileName;
            var updated = Clone(_catalog);
            updated.ProfileNames.Add(profileName);
            if (string.IsNullOrWhiteSpace(updated.ActiveProfileName))
            {
                updated.ActiveProfileName = profileName;
            }

            if (!_registry.TryAdd(profileName))
            {
                throw new InvalidOperationException(
                    $"The profile name '{profileName}' is already registered or reserved by a fixed OptionsName."
                );
            }
            var createdRuntime = _registry.Get(profileName);
            try
            {
                if (hasSourceValue)
                {
                    using var session = await _registry
                        .Get(profileName)
                        .OpenEditSessionAsync(cancellationToken)
                        .ConfigureAwait(false);
                    session.Value = sourceValue;
                    await session.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                await PersistCatalogAsync(updated, cancellationToken).ConfigureAwait(false);
                _catalogRuntimeNames.Add(profileName);
                if (
                    !string.Equals(
                        originalActiveProfileName,
                        updated.ActiveProfileName,
                        StringComparison.Ordinal
                    )
                )
                {
                    EnqueueActiveProfileNotification(updated.ActiveProfileName);
                }
            }
            catch (Exception creationException)
            {
                // A writer may fail after committing. Force the next manager operation to
                // reread the catalog before trusting either the old or proposed state.
                _initialized = false;
                try
                {
                    await _registry.TryRemoveAsync(profileName).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    notificationScope?.Cancel(createdRuntime);
                    throw new AggregateException(
                        $"Profile '{profileName}' could not be created and its runtime could not be cleaned up.",
                        creationException,
                        cleanupException
                    );
                }
                notificationScope?.Cancel(createdRuntime);

                throw;
            }
        }
        finally
        {
            _gate.Release();
            try
            {
                notificationScope?.Dispose();
            }
            finally
            {
                DrainPendingActiveProfileNotifications();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask RemoveProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileName(profileName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IConfiglueOptionsRegistryNotificationDeferral<TModel>? notificationScope = null;
        try
        {
            notificationScope = DeferRegistryNotifications();
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            EnsureProfileExists(profileName);
            if (string.Equals(profileName, _defaultProfileName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The default profile cannot be removed.");
            }

            var updated = Clone(_catalog!);
            string? changedActiveProfile = null;
            updated.ProfileNames.RemoveAll(name =>
                string.Equals(name, profileName, StringComparison.Ordinal)
            );
            if (string.Equals(updated.ActiveProfileName, profileName, StringComparison.Ordinal))
            {
                updated.ActiveProfileName = updated.ProfileNames[0];
                changedActiveProfile = updated.ActiveProfileName;
            }

            await PersistCatalogAsync(updated, cancellationToken).ConfigureAwait(false);
            if (changedActiveProfile is not null)
            {
                EnqueueActiveProfileNotification(changedActiveProfile);
            }
            await _registry.TryRemoveAsync(profileName).ConfigureAwait(false);
            _catalogRuntimeNames.Remove(profileName);
        }
        finally
        {
            _gate.Release();
            try
            {
                notificationScope?.Dispose();
            }
            finally
            {
                DrainPendingActiveProfileNotifications();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask SetActiveProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileName(profileName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IConfiglueOptionsRegistryNotificationDeferral<TModel>? notificationScope = null;
        try
        {
            notificationScope = DeferRegistryNotifications();
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            EnsureProfileExists(profileName);
            if (string.Equals(profileName, _catalog!.ActiveProfileName, StringComparison.Ordinal))
            {
                return;
            }

            var updated = Clone(_catalog);
            updated.ActiveProfileName = profileName;
            await PersistCatalogAsync(updated, cancellationToken).ConfigureAwait(false);
            EnqueueActiveProfileNotification(profileName);
        }
        finally
        {
            _gate.Release();
            try
            {
                notificationScope?.Dispose();
            }
            finally
            {
                DrainPendingActiveProfileNotifications();
            }
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_initialized)
        {
            return;
        }

        var previousActiveProfileName = _catalog?.ActiveProfileName;

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
                _catalogRevision = result.Revision;
            }
            else if (result.Status == StateReadStatus.NotFound)
            {
                catalog = CreateDefaultCatalog();
                needsWrite = true;
                _catalogRevision = result.Revision;
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
                    var writeResult = await _catalogSource
                        .Writer!.WriteAsync(
                            new StateWriteRequest<ConfiglueProfileCatalog>(
                                catalog,
                                result.Revision,
                                CheckRevision: true
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    _catalogRevision = writeResult.Revision;
                }
                catch (StateConflictException) when (attempt < 4)
                {
                    continue;
                }
            }

            await SynchronizeRegistryAsync(catalog).ConfigureAwait(false);
            _catalog = catalog;
            _initialized = true;
            StartCatalogWatcher();
            if (
                previousActiveProfileName is not null
                && !string.Equals(
                    previousActiveProfileName,
                    catalog.ActiveProfileName,
                    StringComparison.Ordinal
                )
            )
            {
                EnqueueActiveProfileNotification(catalog.ActiveProfileName!);
            }
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
                var writeResult = await _catalogSource
                    .Writer!.WriteAsync(
                        new StateWriteRequest<ConfiglueProfileCatalog>(
                            catalog,
                            current.Revision,
                            CheckRevision: true
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                _catalogRevision = writeResult.Revision;
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
            catch
            {
                // A writer may report failure after the catalog reached durable storage.
                // Re-read on the next operation before trusting the cached catalog.
                _initialized = false;
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

    private void StartCatalogWatcher()
    {
        lock (_subscriptionGate)
        {
            if (
                _catalogSource.Watcher is null
                || _catalogWatchTask is not null
                || Volatile.Read(ref _disposed) != 0
            )
            {
                return;
            }

            _catalogWatchTask = WatchCatalogAsync(_watcherCancellation.Token);
        }
    }

    private async Task WatchCatalogAsync(CancellationToken cancellationToken)
    {
        var refreshPending = false;
        while (true)
        {
            if (!refreshPending)
            {
                try
                {
                    await _catalogSource
                        .Watcher!.WaitForChangeAsync(_catalogRevision, cancellationToken)
                        .ConfigureAwait(false);
                    refreshPending = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Trace.TraceError("Configlue profile catalog watcher failed: {0}", exception);
                    var shouldRetry = await DelayCatalogWatcherRetryAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!shouldRetry)
                    {
                        return;
                    }

                    refreshPending = true;
                    continue;
                }
            }

            try
            {
                await RefreshCatalogFromWatcherAsync(cancellationToken).ConfigureAwait(false);
                refreshPending = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue profile catalog refresh failed: {0}", exception);
                var shouldRetry = await DelayCatalogWatcherRetryAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!shouldRetry)
                {
                    return;
                }
            }
        }
    }

    private static async Task<bool> DelayCatalogWatcherRetryAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RefreshCatalogFromWatcherAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IConfiglueOptionsRegistryNotificationDeferral<TModel>? notificationScope = null;
        try
        {
            notificationScope = DeferRegistryNotifications();
            _initialized = false;
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            try
            {
                notificationScope?.Dispose();
            }
            finally
            {
                _ = Task.Run(DrainPendingActiveProfileNotifications, CancellationToken.None);
            }
        }
    }

    private async Task DisposeCoreAsync(Task? catalogWatchTask)
    {
        if (catalogWatchTask is not null)
        {
            await catalogWatchTask.ConfigureAwait(false);
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Release();
        _watcherCancellation.Dispose();
    }

    private void RemoveSubscription(ActiveProfileValueSubscription subscription)
    {
        lock (_subscriptionGate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private async Task SynchronizeRegistryAsync(ConfiglueProfileCatalog catalog)
    {
        var expected = new HashSet<string>(catalog.ProfileNames, StringComparer.Ordinal);
        foreach (var profileName in catalog.ProfileNames)
        {
            if (
                !_registry.TryGet(profileName, out _)
                && !_registry.TryAdd(profileName)
                && !_registry.TryGet(profileName, out _)
            )
            {
                throw new InvalidDataException(
                    $"Profile '{profileName}' conflicts with a fixed OptionsName or could not be registered."
                );
            }
            _catalogRuntimeNames.Add(profileName);
        }

        foreach (
            var registeredName in _catalogRuntimeNames
                .Where(name => !expected.Contains(name))
                .ToArray()
        )
        {
            await _registry.TryRemoveAsync(registeredName).ConfigureAwait(false);
            _catalogRuntimeNames.Remove(registeredName);
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

    private IConfiglueOptionsRegistryNotificationDeferral<TModel>? DeferRegistryNotifications() =>
        (_registry as IConfiglueOptionsRegistryNotificationDeferrer<TModel>)?.DeferNotifications();

    private void EnqueueActiveProfileNotification(string profileName)
    {
        lock (_activeProfileNotificationGate)
        {
            _pendingActiveProfileNotifications.Enqueue(profileName);
        }
    }

    private void DrainPendingActiveProfileNotifications()
    {
        lock (_activeProfileNotificationGate)
        {
            if (_dispatchingActiveProfileNotifications)
            {
                return;
            }
            _dispatchingActiveProfileNotifications = true;
        }

        while (true)
        {
            string profileName;
            lock (_activeProfileNotificationGate)
            {
                if (_pendingActiveProfileNotifications.Count == 0)
                {
                    _dispatchingActiveProfileNotifications = false;
                    return;
                }
                profileName = _pendingActiveProfileNotifications.Dequeue();
            }

            NotifyActiveProfileChanged(profileName);
        }
    }

    private sealed class ActiveProfileValueSubscription : IDisposable
    {
        private readonly ConfiglueProfiledOptions<TModel, TFragment> _owner;
        private readonly Action<TModel> _listener;
        private readonly object _gate = new();
        private string? _profileName;
        private IWritableOptions<TModel>? _profile;
        private IDisposable? _profileSubscription;
        private bool _disposed;

        public ActiveProfileValueSubscription(
            ConfiglueProfiledOptions<TModel, TFragment> owner,
            Action<TModel> listener
        )
        {
            _owner = owner;
            _listener = listener;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _owner.ActiveProfileChanged += OnActiveProfileChanged;
            }

            var profileName = _owner.GetActiveProfileNameAsync().GetAwaiter().GetResult();
            var profile = _owner.GetProfileAsync(profileName).GetAwaiter().GetResult();
            Bind(profileName, profile, notify: false);
        }

        public void Dispose()
        {
            IDisposable? profileSubscription;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                profileSubscription = _profileSubscription;
                _profileSubscription = null;
                _profile = null;
            }

            _owner.ActiveProfileChanged -= OnActiveProfileChanged;
            profileSubscription?.Dispose();
            _owner.RemoveSubscription(this);
        }

        private void OnActiveProfileChanged(string profileName)
        {
            try
            {
                var profile = _owner.GetProfileAsync(profileName).GetAwaiter().GetResult();
                Bind(profileName, profile, notify: true);
            }
            catch (Exception exception)
            {
                Trace.TraceError(
                    "Configlue active-profile value subscription failed: {0}",
                    exception
                );
            }
        }

        private void Bind(string profileName, IWritableOptions<TModel> profile, bool notify)
        {
            IDisposable? previousSubscription;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                if (
                    string.Equals(_profileName, profileName, StringComparison.Ordinal)
                    && ReferenceEquals(_profile, profile)
                )
                {
                    return;
                }

                previousSubscription = _profileSubscription;
                _profileName = profileName;
                _profile = profile;
                _profileSubscription = profile.OnChange(value =>
                    OnProfileValueChanged(profile, value)
                );
            }

            previousSubscription?.Dispose();
            if (notify)
            {
                NotifyListener(profile.GetValueAsync().AsTask().GetAwaiter().GetResult());
            }
        }

        private void OnProfileValueChanged(IWritableOptions<TModel> profile, TModel value)
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_profile, profile))
                {
                    return;
                }
            }

            NotifyListener(value);
        }

        private void NotifyListener(TModel value)
        {
            try
            {
                _listener(value);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue active-profile value listener failed: {0}", exception);
            }
        }
    }

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
