using System.Diagnostics;
using Configlue.CompilerServices;

namespace Configlue;

/// <summary>Manages profile state instances using a persisted catalog.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
internal sealed partial class ConfiglueProfiledState<TModel, TFragment>
    : IConfiglueProfiledState<TModel>,
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

    private readonly IConfiglueStateRegistry<TModel> _registry;
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
    public ConfiglueProfiledState(
        IConfiglueStateRegistry<TModel> registry,
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
            _watcherCancellation.Cancel();
            var subscriptions = _subscriptions.ToArray();
            disposeTask = DisposeCoreAsync(_catalogWatchTask, subscriptions);
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
        IConfiglueStateRegistryNotificationDeferral<TModel>? notificationScope = null;
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
        IConfiglueStateRegistryNotificationDeferral<TModel>? notificationScope = null;
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
    public async ValueTask<IWritableState<TModel>> GetProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    )
    {
        ValidateProfileName(profileName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IConfiglueStateRegistryNotificationDeferral<TModel>? notificationScope = null;
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
    public async ValueTask<IWritableState<TModel>> GetActiveProfileAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IConfiglueStateRegistryNotificationDeferral<TModel>? notificationScope = null;
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
        IConfiglueStateRegistryNotificationDeferral<TModel>? notificationScope = null;
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
                    $"The profile name '{profileName}' is already registered or reserved by a fixed StateName."
                );
            }
            var createdRuntime = _registry.Get(profileName);
            try
            {
                if (hasSourceValue)
                {
                    using var session = await AsAdvancedState(_registry.Get(profileName))
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
#if NETSTANDARD2_0
                    EnqueueActiveProfileNotification(updated.ActiveProfileName!);
#else
                    EnqueueActiveProfileNotification(updated.ActiveProfileName);
#endif
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
        IConfiglueStateRegistryNotificationDeferral<TModel>? notificationScope = null;
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
        IConfiglueStateRegistryNotificationDeferral<TModel>? notificationScope = null;
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
                    $"Profile '{profileName}' conflicts with a fixed StateName or could not be registered."
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

    private IConfiglueStateRegistryNotificationDeferral<TModel>? DeferRegistryNotifications() =>
        (_registry as IConfiglueStateRegistryNotificationDeferrer<TModel>)?.DeferNotifications();

    private static IConfiglueEditSessions<TModel> AsAdvancedState(IWritableState<TModel> state) =>
        state as IConfiglueEditSessions<TModel>
        ?? throw new InvalidOperationException(
            "The profile registry returned a state without edit-session support."
        );

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
