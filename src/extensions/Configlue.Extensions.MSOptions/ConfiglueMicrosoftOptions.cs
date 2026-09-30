using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Extensions.MSOptions;

internal sealed class ConfiglueMicrosoftOptionsResolver<TModel>
{
    private readonly IServiceProvider _services;

    public ConfiglueMicrosoftOptionsResolver(IServiceProvider services) => _services = services;

    public IReadOnlyState<TModel> Resolve(string? name)
    {
        var normalizedName = name ?? Options.DefaultName;
        if (
            normalizedName == Options.DefaultName
            && _services.GetService<IReadOnlyState<TModel>>() is { } defaultOptions
        )
        {
            return defaultOptions;
        }

        if (_services.GetKeyedService<IReadOnlyState<TModel>>(normalizedName) is { } keyedOptions)
        {
            return keyedOptions;
        }

        if (
            normalizedName != Options.DefaultName
            && _services.GetService<IConfiglueStateRegistry<TModel>>() is { } registry
            && registry.TryGet(normalizedName, out var registeredOptions)
            && registeredOptions is not null
        )
        {
            return registeredOptions;
        }

        throw new KeyNotFoundException(
            $"No Configlue state named '{normalizedName}' is registered."
        );
    }

    public IConfiglueStateRegistry<TModel>? Registry =>
        _services.GetService<IConfiglueStateRegistry<TModel>>();
}

internal sealed class ConfiglueMicrosoftOptionsValue<TModel> : IOptions<TModel>
    where TModel : class
{
    public ConfiglueMicrosoftOptionsValue(ConfiglueMicrosoftOptionsResolver<TModel> resolver)
    {
        _value = new Lazy<TModel>(
            () => Read(resolver, Options.DefaultName),
            LazyThreadSafetyMode.ExecutionAndPublication
        );
    }

    private readonly Lazy<TModel> _value;

    public TModel Value => _value.Value;

    private static TModel Read(
        ConfiglueMicrosoftOptionsResolver<TModel> optionsResolver,
        string name
    // Microsoft Options exposes synchronous getters; this opt-in framework adapter deliberately blocks for its snapshot.
    ) => optionsResolver.Resolve(name).GetValueAsync().AsTask().GetAwaiter().GetResult();
}

internal sealed class ConfiglueMicrosoftOptionsSnapshot<TModel> : IOptionsSnapshot<TModel>
    where TModel : class
{
    private readonly ConfiglueMicrosoftOptionsResolver<TModel> _resolver;
    private readonly ConcurrentDictionary<string, Lazy<TModel>> _values = new(
        StringComparer.Ordinal
    );

    public ConfiglueMicrosoftOptionsSnapshot(ConfiglueMicrosoftOptionsResolver<TModel> resolver) =>
        _resolver = resolver;

    public TModel Value => Get(Options.DefaultName);

    public TModel Get(string? name)
    {
        var normalizedName = name ?? Options.DefaultName;
        return _values
            .GetOrAdd(
                normalizedName,
                key => new Lazy<TModel>(
                    // Microsoft Options exposes synchronous getters; this opt-in framework adapter deliberately blocks for its snapshot.
                    () => _resolver.Resolve(key).GetValueAsync().AsTask().GetAwaiter().GetResult(),
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            )
            .Value;
    }
}

internal sealed class ConfiglueMicrosoftOptionsMonitor<TModel>
    : IOptionsMonitor<TModel>,
        IDisposable
    where TModel : class
{
    private readonly ConfiglueMicrosoftOptionsResolver<TModel> _resolver;
    private readonly string[] _namedProfileNames;
    private readonly HashSet<string> _namedProfileNameSet;
    private readonly IConfiglueStateRegistry<TModel>? _registry;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, MonitorCacheEntry> _namedCache = new(
        StringComparer.Ordinal
    );
    private MonitorCacheEntry? _defaultCache;
    private bool _disposed;

    public ConfiglueMicrosoftOptionsMonitor(
        ConfiglueMicrosoftOptionsResolver<TModel> resolver,
        IEnumerable<ConfiglueNamedStateProfile<TModel>> namedProfiles
    )
    {
        _resolver = resolver;
        _registry = resolver.Registry;
        if (_registry is not null)
        {
            _registry.StateRemoved += OnStateRemoved;
        }

        _namedProfileNames = namedProfiles
            .Select(profile =>
                profile.ModelType == typeof(TModel)
                    ? profile.Name
                    : throw new InvalidOperationException(
                        $"Named profile '{profile.Name}' belongs to '{profile.ModelType}', not '{typeof(TModel)}'."
                    )
            )
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _namedProfileNameSet = new HashSet<string>(_namedProfileNames, StringComparer.Ordinal);
    }

    public TModel CurrentValue => GetDefaultCache().Value;

    public TModel Get(string? name)
    {
        var normalizedName = name ?? Options.DefaultName;
        if (normalizedName == Options.DefaultName)
        {
            return GetDefaultCache().Value;
        }

        var options = _resolver.Resolve(normalizedName);
        return GetNamedCache(normalizedName, options).Value;
    }

    public void Dispose()
    {
        MonitorCacheEntry[] entries;
        lock (_cacheGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = _namedCache.Values.ToArray();
            _namedCache.Clear();
            if (_defaultCache is not null)
            {
                entries = [.. entries, _defaultCache];
                _defaultCache = null;
            }
        }

        if (_registry is not null)
        {
            _registry.StateRemoved -= OnStateRemoved;
        }

        foreach (var entry in entries)
        {
            entry.Dispose();
        }
    }

    private MonitorCacheEntry GetDefaultCache()
    {
        var cached = Volatile.Read(ref _defaultCache);
        if (cached is not null)
        {
            return cached;
        }

        lock (_cacheGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cached = _defaultCache;
            if (cached is null)
            {
                cached = new MonitorCacheEntry(_resolver.Resolve(Options.DefaultName));
                Volatile.Write(ref _defaultCache, cached);
            }

            return cached;
        }
    }

    private MonitorCacheEntry GetNamedCache(string name, IReadOnlyState<TModel> options)
    {
        var isRegistryProfile = false;
        lock (_cacheGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registry is not null)
            {
                if (_registry.TryGet(name, out var registered) && registered is not null)
                {
                    options = registered;
                    isRegistryProfile = true;
                }
                else if (!_namedProfileNameSet.Contains(name))
                {
                    throw new KeyNotFoundException(
                        $"No Configlue state named '{name}' is registered."
                    );
                }
            }

            if (_namedCache.TryGetValue(name, out var cached))
            {
                if (ReferenceEquals(cached.Options, options))
                {
                    return cached;
                }

                _namedCache.Remove(name);
                cached.Dispose();
            }

            var created = new MonitorCacheEntry(options, allowCache: !isRegistryProfile);
            _namedCache.Add(name, created);
            return created;
        }
    }

    private void OnStateRemoved(string name)
    {
        MonitorCacheEntry? removed = null;
        lock (_cacheGate)
        {
            if (_namedCache.TryGetValue(name, out var cached))
            {
                _namedCache.Remove(name);
                removed = cached;
            }
        }

        removed?.Dispose();
    }

    private sealed class MonitorCacheEntry : IDisposable
    {
        private readonly object _gate = new();
        private readonly IDisposable? _subscription;
        private readonly bool _cacheable;
        private readonly IConfiglueValueCloneProvider<TModel>? _cloneProvider;
        private TModel? _value;
        private int _changeVersion;

        public MonitorCacheEntry(IReadOnlyState<TModel> options, bool allowCache = true)
        {
            Options = options;
            _cloneProvider = options as IConfiglueValueCloneProvider<TModel>;
            var diagnostics = (options as IConfiglueDiagnostics<TModel>)?.GetDiagnostics();
            _cacheable =
                allowCache
                && _cloneProvider is not null
                && diagnostics?.Sources.Any(static source => source.CanWatch) == true;
            if (_cacheable)
            {
                _subscription = options.OnChange(OnChanged);
            }
        }

        public IReadOnlyState<TModel> Options { get; }

        public TModel Value
        {
            get
            {
                if (!_cacheable)
                {
                    return Read(Options);
                }

                var value = Volatile.Read(ref _value);
                if (value is not null)
                {
                    return _cloneProvider!.CloneValue(value);
                }

                var changeVersion = Volatile.Read(ref _changeVersion);
                var loaded = Read(Options);
                lock (_gate)
                {
                    value = _value;
                    if (value is not null)
                    {
                        return _cloneProvider!.CloneValue(value);
                    }

                    if (changeVersion != _changeVersion)
                    {
                        return _value!;
                    }

                    Volatile.Write(ref _value, loaded);
                    return _cloneProvider!.CloneValue(loaded);
                }
            }
        }

        public void Dispose() => _subscription?.Dispose();

        private static TModel Read(IReadOnlyState<TModel> options) =>
            // Microsoft Options exposes synchronous getters; this opt-in framework adapter deliberately blocks for its snapshot.
            options.GetValueAsync().AsTask().GetAwaiter().GetResult();

        private void OnChanged(TModel value)
        {
            lock (_gate)
            {
                _changeVersion++;
                Volatile.Write(ref _value, value);
            }
        }
    }

    public IDisposable? OnChange(Action<TModel, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        EnsureKnownCacheEntries();
        var subscription = new ChangeSubscription(
            _resolver,
            listener,
            _namedProfileNames,
            EnsureCacheEntry
        );
        subscription.Start();
        return subscription;
    }

    private void EnsureKnownCacheEntries()
    {
        try
        {
            _ = GetDefaultCache();
        }
        catch (KeyNotFoundException)
        {
            // A registry-only setup can expose named profiles without a default profile.
        }

        foreach (var name in _namedProfileNames)
        {
            try
            {
                var options = _resolver.Resolve(name);
                _ = GetNamedCache(name, options);
            }
            catch (KeyNotFoundException)
            {
                // A registration may have been removed since the service provider was built.
            }
        }

        if (_registry is not null)
        {
            foreach (var name in _registry.StateNames)
            {
                if (
                    name != Options.DefaultName
                    && _registry.TryGet(name, out var options)
                    && options is not null
                )
                {
                    _ = GetNamedCache(name, options);
                }
            }
        }
    }

    private void EnsureCacheEntry(string name, IReadOnlyState<TModel> options)
    {
        if (name == Options.DefaultName)
        {
            _ = GetDefaultCache();
        }
        else
        {
            _ = GetNamedCache(name, options);
        }
    }

    private sealed class ChangeSubscription : IDisposable
    {
        private readonly ConfiglueMicrosoftOptionsResolver<TModel> _resolver;
        private readonly Action<TModel, string?> _listener;
        private readonly Action<string, IReadOnlyState<TModel>> _ensureCacheEntry;
        private readonly object _gate = new();
        private readonly Dictionary<string, ProfileSubscription> _subscriptions = new(
            StringComparer.Ordinal
        );
        private readonly Dictionary<string, object> _profileOperationTokens = new(
            StringComparer.Ordinal
        );
        private readonly IConfiglueStateRegistry<TModel>? _registry;
        private bool _disposed;

        private sealed class ProfileSubscription(IReadOnlyState<TModel> options)
        {
            public IReadOnlyState<TModel> Options { get; } = options;

            public IDisposable? ChangeSubscription { get; set; }
        }

        public ChangeSubscription(
            ConfiglueMicrosoftOptionsResolver<TModel> resolver,
            Action<TModel, string?> listener,
            IEnumerable<string> namedProfileNames,
            Action<string, IReadOnlyState<TModel>> ensureCacheEntry
        )
        {
            _resolver = resolver;
            _listener = listener;
            _ensureCacheEntry = ensureCacheEntry;
            _registry = resolver.Registry;
            NamedProfileNames = namedProfileNames.ToArray();
        }

        private string[] NamedProfileNames { get; }

        public void Start()
        {
            try
            {
                Subscribe(Options.DefaultName, _resolver.Resolve(Options.DefaultName));
            }
            catch (KeyNotFoundException)
            {
                // A registry-only setup can expose named profiles without a default profile.
            }

            foreach (var name in NamedProfileNames)
            {
                Subscribe(name, _resolver.Resolve(name));
            }

            if (_registry is not null)
            {
                _registry.StateAdded += OnStateAdded;
                _registry.StateRemoved += OnStateRemoved;
                foreach (var name in _registry.StateNames)
                {
                    if (
                        name != Options.DefaultName
                        && _registry.TryGet(name, out var options)
                        && options is not null
                    )
                    {
                        Subscribe(name, options, registryProfile: true);
                    }
                }
            }
        }

        public void Dispose()
        {
            IDisposable[] subscriptions;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                subscriptions = _subscriptions
                    .Values.Select(entry => entry.ChangeSubscription)
                    .OfType<IDisposable>()
                    .ToArray();
                _subscriptions.Clear();
                _profileOperationTokens.Clear();
            }

            if (_registry is not null)
            {
                _registry.StateAdded -= OnStateAdded;
                _registry.StateRemoved -= OnStateRemoved;
            }

            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }

        private void OnStateAdded(string name, IWritableState<TModel> options)
        {
            if (name != Options.DefaultName)
            {
                Subscribe(name, options, registryProfile: true);
            }
        }

        private void OnStateRemoved(string name)
        {
            var operationToken = BeginProfileOperation(name);
            if (operationToken is null)
            {
                return;
            }

            if (TryGetRegisteredProfile(name, out var current) && current is not null)
            {
                BindProfile(name, current, operationToken);
                return;
            }

            IDisposable? subscription = null;
            lock (_gate)
            {
                if (!IsCurrentProfileOperation(name, operationToken))
                {
                    return;
                }

                _profileOperationTokens.Remove(name);
                if (_subscriptions.TryGetValue(name, out var removed))
                {
                    _subscriptions.Remove(name);
                    subscription = removed.ChangeSubscription;
                }
            }

            subscription?.Dispose();
        }

        private void Subscribe(
            string name,
            IReadOnlyState<TModel> options,
            bool registryProfile = false
        )
        {
            var operationToken = BeginProfileOperation(name);
            if (operationToken is null)
            {
                return;
            }

            if (registryProfile)
            {
                if (!TryGetRegisteredProfile(name, out var current) || current is null)
                {
                    CancelProfileOperation(name, operationToken);
                    return;
                }

                options = current;
            }

            BindProfile(name, options, operationToken);
        }

        private void BindProfile(string name, IReadOnlyState<TModel> options, object operationToken)
        {
            ProfileSubscription entry;
            IDisposable? previousSubscription = null;
            lock (_gate)
            {
                if (!IsCurrentProfileOperation(name, operationToken))
                {
                    return;
                }

                if (_subscriptions.TryGetValue(name, out var existing))
                {
                    if (ReferenceEquals(existing.Options, options))
                    {
                        _profileOperationTokens.Remove(name);
                        return;
                    }

                    _subscriptions.Remove(name);
                    previousSubscription = existing.ChangeSubscription;
                }

                entry = new ProfileSubscription(options);
                _subscriptions.Add(name, entry);
                _profileOperationTokens.Remove(name);
            }

            previousSubscription?.Dispose();
            IDisposable subscription;
            try
            {
                _ensureCacheEntry(name, options);
                subscription = options.OnChange(value => _listener(value, name));
            }
            catch
            {
                lock (_gate)
                {
                    if (
                        _subscriptions.TryGetValue(name, out var current)
                        && ReferenceEquals(current, entry)
                    )
                    {
                        _subscriptions.Remove(name);
                    }
                }

                throw;
            }

            var disposeSubscription = false;
            lock (_gate)
            {
                if (
                    _disposed
                    || !_subscriptions.TryGetValue(name, out var current)
                    || !ReferenceEquals(current, entry)
                )
                {
                    disposeSubscription = true;
                }
                else
                {
                    entry.ChangeSubscription = subscription;
                }
            }

            if (disposeSubscription)
            {
                subscription.Dispose();
            }
        }

        private object? BeginProfileOperation(string name)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return null;
                }

                var operationToken = new object();
                _profileOperationTokens[name] = operationToken;
                return operationToken;
            }
        }

        private void CancelProfileOperation(string name, object operationToken)
        {
            lock (_gate)
            {
                if (IsCurrentProfileOperation(name, operationToken))
                {
                    _profileOperationTokens.Remove(name);
                }
            }
        }

        private bool IsCurrentProfileOperation(string name, object operationToken) =>
            !_disposed
            && _profileOperationTokens.TryGetValue(name, out var current)
            && ReferenceEquals(current, operationToken);

        private bool TryGetRegisteredProfile(string name, out IWritableState<TModel>? options)
        {
            options = null;
            if (_registry is null)
            {
                return false;
            }

            try
            {
                return _registry.TryGet(name, out options);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }
}
