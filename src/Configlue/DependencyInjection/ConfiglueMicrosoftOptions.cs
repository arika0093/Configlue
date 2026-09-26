using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue;

internal sealed record ConfiglueNamedOptionsProfile<TModel>(string Name);

internal sealed class ConfiglueMicrosoftOptionsResolver<TModel>(IServiceProvider services)
{
    public IReadOnlyOptions<TModel> Resolve(string? name)
    {
        var normalizedName = name ?? Options.DefaultName;
        if (normalizedName == Options.DefaultName && services.GetService<IReadOnlyOptions<TModel>>() is { } defaultOptions)
        {
            return defaultOptions;
        }

        if (services.GetKeyedService<IReadOnlyOptions<TModel>>(normalizedName) is { } keyedOptions)
        {
            return keyedOptions;
        }

        if (normalizedName != Options.DefaultName &&
            services.GetService<IConfiglueOptionsRegistry<TModel>>() is { } registry &&
            registry.TryGet(normalizedName, out var registeredOptions) && registeredOptions is not null)
        {
            return registeredOptions;
        }

        throw new KeyNotFoundException($"No Configlue options profile named '{normalizedName}' is registered.");
    }

    public IConfiglueOptionsRegistry<TModel>? Registry => services.GetService<IConfiglueOptionsRegistry<TModel>>();
}

internal sealed class ConfiglueMicrosoftOptionsValue<TModel>(
    ConfiglueMicrosoftOptionsResolver<TModel> resolver) : IOptions<TModel>
    where TModel : class
{
    private readonly Lazy<TModel> _value = new(() => Read(resolver, Options.DefaultName), LazyThreadSafetyMode.ExecutionAndPublication);

    public TModel Value => _value.Value;

    private static TModel Read(ConfiglueMicrosoftOptionsResolver<TModel> optionsResolver, string name) =>
        optionsResolver.Resolve(name).GetValueAsync().AsTask().GetAwaiter().GetResult();
}

internal sealed class ConfiglueMicrosoftOptionsSnapshot<TModel>(
    ConfiglueMicrosoftOptionsResolver<TModel> resolver) : IOptionsSnapshot<TModel>
    where TModel : class
{
    private readonly ConcurrentDictionary<string, Lazy<TModel>> _values = new(StringComparer.Ordinal);

    public TModel Value => Get(Options.DefaultName);

    public TModel Get(string? name)
    {
        var normalizedName = name ?? Options.DefaultName;
        return _values.GetOrAdd(
            normalizedName,
            key => new Lazy<TModel>(
                () => resolver.Resolve(key).GetValueAsync().AsTask().GetAwaiter().GetResult(),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}

internal sealed class ConfiglueMicrosoftOptionsMonitor<TModel>(
    ConfiglueMicrosoftOptionsResolver<TModel> resolver,
    IEnumerable<ConfiglueNamedOptionsProfile<TModel>> namedProfiles) : IOptionsMonitor<TModel>
    where TModel : class
{
    private readonly string[] _namedProfileNames = namedProfiles.Select(static profile => profile.Name)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public TModel CurrentValue => Get(Options.DefaultName);

    public TModel Get(string? name) =>
        resolver.Resolve(name).GetValueAsync().AsTask().GetAwaiter().GetResult();

    public IDisposable? OnChange(Action<TModel, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var subscription = new ChangeSubscription(resolver, listener, _namedProfileNames);
        subscription.Start();
        return subscription;
    }

    private sealed class ChangeSubscription : IDisposable
    {
        private readonly ConfiglueMicrosoftOptionsResolver<TModel> _resolver;
        private readonly Action<TModel, string?> _listener;
        private readonly object _gate = new();
        private readonly Dictionary<string, IDisposable?> _subscriptions = new(StringComparer.Ordinal);
        private readonly IConfiglueOptionsRegistry<TModel>? _registry;
        private bool _disposed;

        public ChangeSubscription(
            ConfiglueMicrosoftOptionsResolver<TModel> resolver,
            Action<TModel, string?> listener,
            IEnumerable<string> namedProfileNames)
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
                    if (name != Options.DefaultName && _registry.TryGet(name, out var options) && options is not null)
                    {
                        Subscribe(name, options);
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
                subscriptions = _subscriptions.Values.OfType<IDisposable>().ToArray();
                _subscriptions.Clear();
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
                Subscribe(name, options);
            }
        }

        private void OnProfileRemoved(string name)
        {
            IDisposable? subscription = null;
            lock (_gate)
            {
                if (_subscriptions.Remove(name, out var removed))
                {
                    subscription = removed;
                }
            }

            subscription?.Dispose();
        }

        private void Subscribe(string name, IReadOnlyOptions<TModel> options)
        {
            lock (_gate)
            {
                if (_disposed || !_subscriptions.TryAdd(name, null))
                {
                    return;
                }
            }

            var subscription = options.OnChange(value => _listener(value, name));
            lock (_gate)
            {
                if (_disposed || !_subscriptions.ContainsKey(name))
                {
                    subscription.Dispose();
                }
                else
                {
                    _subscriptions[name] = subscription;
                }
            }
        }
    }
}
