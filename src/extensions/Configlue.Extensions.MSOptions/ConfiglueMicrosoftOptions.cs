using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Extensions.MSOptions;

// Microsoft Options getters are synchronous, so this opt-in adapter deliberately blocks
// on the asynchronous Configlue read (GetAwaiter().GetResult()). Do not add async
// choreography to hide that blocking; it is inherent to the Microsoft Options contract.
//
// Interop contract:
// - The default Configlue state maps to IOptions<T> / IOptionsMonitor<T> default lookups.
// - Statically registered named states (builder.Add<T> with StateName, exposed as keyed
//   IReadOnlyState<TModel> services) map to named Microsoft Options lookups.
// - Configlue OnChange notifications map to IOptionsMonitor<T>.OnChange callbacks.
// Dynamic IConfiglueStateRegistry<TModel> lifecycle (states added/removed at runtime,
// including catalog-managed profiles) is intentionally NOT mirrored here.

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

        throw new KeyNotFoundException(
            $"No Configlue state named '{normalizedName}' is registered."
        );
    }
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

internal sealed class ConfiglueMicrosoftOptionsMonitor<TModel> : IOptionsMonitor<TModel>
    where TModel : class
{
    private readonly ConfiglueMicrosoftOptionsResolver<TModel> _resolver;
    private readonly string[] _namedStateNames;

    public ConfiglueMicrosoftOptionsMonitor(
        ConfiglueMicrosoftOptionsResolver<TModel> resolver,
        IEnumerable<ConfiglueNamedState<TModel>> namedStates
    )
    {
        _resolver = resolver;
        _namedStateNames = namedStates
            .Select(state =>
                state.ModelType == typeof(TModel)
                    ? state.Name
                    : throw new InvalidOperationException(
                        $"Named state '{state.Name}' belongs to '{state.ModelType}', not '{typeof(TModel)}'."
                    )
            )
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public TModel CurrentValue => Get(Options.DefaultName);

    public TModel Get(string? name) =>
        // Microsoft Options exposes synchronous getters; this opt-in framework adapter deliberately blocks for its snapshot.
        _resolver.Resolve(name).GetValueAsync().AsTask().GetAwaiter().GetResult();

    public IDisposable? OnChange(Action<TModel, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var subscriptions = new List<IDisposable>();
        try
        {
            Subscribe(Options.DefaultName, subscriptions, listener);
            foreach (var name in _namedStateNames)
            {
                Subscribe(name, subscriptions, listener);
            }
        }
        catch
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }

            throw;
        }

        return new ChangeSubscription(subscriptions);
    }

    private void Subscribe(
        string name,
        List<IDisposable> subscriptions,
        Action<TModel, string?> listener
    )
    {
        IReadOnlyState<TModel> options;
        try
        {
            options = _resolver.Resolve(name);
        }
        catch (KeyNotFoundException)
        {
            // A statically declared named state may no longer resolve (for example when
            // only a subset of states was registered); other states still notify.
            return;
        }

        var capturedName = name;
        subscriptions.Add(options.OnChange(value => listener(value, capturedName)));
    }

    private sealed class ChangeSubscription(List<IDisposable> subscriptions) : IDisposable
    {
        private List<IDisposable>? _subscriptions = subscriptions;

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _subscriptions, null);
            if (owned is null)
            {
                return;
            }

            foreach (var subscription in owned)
            {
                subscription.Dispose();
            }
        }
    }
}
