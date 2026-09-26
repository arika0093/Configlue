using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue;

internal sealed record ConfiglueNamedOptionsProfile<TModel>(string Name)
{
    public Type ModelType => typeof(TModel);
}

internal sealed class ConfiglueMicrosoftOptionsResolver<TModel>
{
    private readonly IServiceProvider _services;

    public ConfiglueMicrosoftOptionsResolver(IServiceProvider services) => _services = services;

    public IReadOnlyOptions<TModel> Resolve(string? name)
    {
        var normalizedName = name ?? Options.DefaultName;
        if (
            normalizedName == Options.DefaultName
            && _services.GetService<IReadOnlyOptions<TModel>>() is { } defaultOptions
        )
        {
            return defaultOptions;
        }

        if (_services.GetKeyedService<IReadOnlyOptions<TModel>>(normalizedName) is { } keyedOptions)
        {
            return keyedOptions;
        }

        if (
            normalizedName != Options.DefaultName
            && _services.GetService<IConfiglueOptionsRegistry<TModel>>() is { } registry
            && registry.TryGet(normalizedName, out var registeredOptions)
            && registeredOptions is not null
        )
        {
            return registeredOptions;
        }

        throw new KeyNotFoundException(
            $"No Configlue options profile named '{normalizedName}' is registered."
        );
    }

    public IConfiglueOptionsRegistry<TModel>? Registry =>
        _services.GetService<IConfiglueOptionsRegistry<TModel>>();
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
                    () => _resolver.Resolve(key).GetValueAsync().AsTask().GetAwaiter().GetResult(),
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            )
            .Value;
    }
}

internal sealed class ConfiglueMicrosoftOptionsMonitor<TModel> : IOptionsMonitor<TModel>
    where TModel : class
{
    private readonly ConfiglueMicrosoftOptionsResolver<TModel> _resolver;
    private readonly string[] _namedProfileNames;

    public ConfiglueMicrosoftOptionsMonitor(
        ConfiglueMicrosoftOptionsResolver<TModel> resolver,
        IEnumerable<ConfiglueNamedOptionsProfile<TModel>> namedProfiles
    )
    {
        _resolver = resolver;
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
    }

    public TModel CurrentValue => Get(Options.DefaultName);

    public TModel Get(string? name) =>
        _resolver.Resolve(name).GetValueAsync().AsTask().GetAwaiter().GetResult();

    public IDisposable? OnChange(Action<TModel, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var subscription = new ChangeSubscription(_resolver, listener, _namedProfileNames);
        subscription.Start();
        return subscription;
    }

    private sealed class ChangeSubscription : IDisposable
    {
        private readonly ConfiglueMicrosoftOptionsResolver<TModel> _resolver;
        private readonly Action<TModel, string?> _listener;
        private readonly object _gate = new();
        private readonly Dictionary<string, ProfileSubscription> _subscriptions = new(
            StringComparer.Ordinal
        );
        private readonly Dictionary<string, object> _profileOperationTokens = new(
            StringComparer.Ordinal
        );
        private readonly IConfiglueOptionsRegistry<TModel>? _registry;
        private bool _disposed;

        private sealed class ProfileSubscription(IReadOnlyOptions<TModel> options)
        {
            public IReadOnlyOptions<TModel> Options { get; } = options;

            public IDisposable? ChangeSubscription { get; set; }
        }

        public ChangeSubscription(
            ConfiglueMicrosoftOptionsResolver<TModel> resolver,
            Action<TModel, string?> listener,
            IEnumerable<string> namedProfileNames
        )
        {
            _resolver = resolver;
            _listener = listener;
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
                _registry.ProfileAdded += OnProfileAdded;
                _registry.ProfileRemoved += OnProfileRemoved;
                foreach (var name in _registry.ProfileNames)
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
                _registry.ProfileAdded -= OnProfileAdded;
                _registry.ProfileRemoved -= OnProfileRemoved;
            }

            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }

        private void OnProfileAdded(string name, IWritableOptions<TModel> options)
        {
            if (name != Options.DefaultName)
            {
                Subscribe(name, options, registryProfile: true);
            }
        }

        private void OnProfileRemoved(string name)
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
                if (_subscriptions.Remove(name, out var removed))
                {
                    subscription = removed.ChangeSubscription;
                }
            }

            subscription?.Dispose();
        }

        private void Subscribe(
            string name,
            IReadOnlyOptions<TModel> options,
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

        private void BindProfile(
            string name,
            IReadOnlyOptions<TModel> options,
            object operationToken
        )
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

        private bool TryGetRegisteredProfile(string name, out IWritableOptions<TModel>? options)
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
