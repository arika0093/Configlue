using Microsoft.Extensions.DependencyInjection;

namespace Configlue;

/// <summary>An isolated, disposable set of registered configuration options.</summary>
public sealed class ConfiglueContext : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<(Type ModelType, string Name), object> _options;
    private readonly Dictionary<Type, object> _registries;
    private readonly Dictionary<Type, object> _profileManagers;
    private readonly object[] _runtimes;
    private readonly IDisposable[] _ownedResources;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;

    internal IReadOnlyList<IDisposable> OwnedResourcesForTests => _ownedResources;

    private ConfiglueContext(
        Dictionary<(Type ModelType, string Name), object> options,
        Dictionary<Type, object> registries,
        Dictionary<Type, object> profileManagers,
        object[] runtimes,
        IDisposable[] ownedResources
    )
    {
        _options = options;
        _registries = registries;
        _profileManagers = profileManagers;
        _runtimes = runtimes;
        _ownedResources = ownedResources;
    }

    /// <summary>Gets the writable options for a model and optional named instance.</summary>
    public IWritableOptions<TModel> GetOptions<TModel>(string? optionsName = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = (typeof(TModel), optionsName ?? string.Empty);
        if (_options.TryGetValue(key, out var options))
        {
            return (IWritableOptions<TModel>)options;
        }
        if (
            _registries.TryGetValue(typeof(TModel), out var registry)
            && ((IConfiglueOptionsRegistry<TModel>)registry).TryGet(
                key.Item2,
                out var dynamicOptions
            )
            && dynamicOptions is not null
        )
        {
            return dynamicOptions;
        }
        throw new KeyNotFoundException(
            $"Model '{typeof(TModel)}' with options name '{key.Item2}' is not registered."
        );
    }

    /// <summary>Reads resolved state and generated provenance details.</summary>
    public IConfiglueInspection<TModel> GetInspection<TModel>(string? optionsName = null) =>
        (IConfiglueInspection<TModel>)GetOptions<TModel>(optionsName);

    /// <summary>Opens long-lived drafts of resolved configuration.</summary>
    public IConfiglueEditSessions<TModel> GetEditSessions<TModel>(string? optionsName = null) =>
        (IConfiglueEditSessions<TModel>)GetOptions<TModel>(optionsName);

    /// <summary>Reports source topology and background reload failures.</summary>
    public IConfiglueDiagnostics<TModel> GetDiagnostics<TModel>(string? optionsName = null) =>
        (IConfiglueDiagnostics<TModel>)GetOptions<TModel>(optionsName);

    /// <summary>Administers source-local writes and source migrations.</summary>
    public IConfiglueSources<TModel> GetSources<TModel>(string? optionsName = null) =>
        (IConfiglueSources<TModel>)GetOptions<TModel>(optionsName);

    internal IConfiglueRuntimeOptions<TModel> GetRuntimeOptions<TModel>(
        string? optionsName = null
    ) => (IConfiglueRuntimeOptions<TModel>)GetOptions<TModel>(optionsName);

    /// <summary>Gets the runtime registry for dynamic named options.</summary>
    public IConfiglueOptionsRegistry<TModel> GetOptionsRegistry<TModel>()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _registries.TryGetValue(typeof(TModel), out var registry)
            ? (IConfiglueOptionsRegistry<TModel>)registry
            : throw new InvalidOperationException(
                $"Dynamic named options are not enabled for model '{typeof(TModel)}'."
            );
    }

    /// <summary>Gets the persisted profile manager for a configured model.</summary>
    public IConfiglueProfiledOptions<TModel> GetProfiledOptions<TModel>()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _profileManagers.TryGetValue(typeof(TModel), out var manager)
            ? (IConfiglueProfiledOptions<TModel>)manager
            : throw new InvalidOperationException(
                $"Persisted profiles are not enabled for model '{typeof(TModel)}'."
            );
    }

    /// <summary>Disposes the options and watchers owned by this context.</summary>
    // Synchronous construction-failure cleanup or IDisposable boundary; normal source I/O stays asynchronous.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Asynchronously disposes the options and watchers owned by this context.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_disposeGate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            Volatile.Write(ref _disposed, 1);
            completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _disposeTask = completion.Task;
        }

        _ = FinishDisposeAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task FinishDisposeAsync(TaskCompletionSource completion)
    {
        var errors = new List<Exception>();
        foreach (var runtime in _runtimes)
        {
            try
            {
                if (runtime is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (runtime is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        foreach (var resource in _ownedResources)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        if (errors.Count > 0)
        {
            completion.TrySetException(
                new AggregateException("One or more Configlue resources failed to dispose.", errors)
            );
            return;
        }

        completion.TrySetResult();
    }

    internal static ConfiglueContext Create(
        IReadOnlyList<IConfiglueModelRegistration> registrations,
        IServiceProvider? serviceProvider
    )
    {
        var options = new Dictionary<(Type ModelType, string Name), object>();
        var registries = new Dictionary<Type, object>();
        var profileManagers = new Dictionary<Type, object>();
        var runtimes = new List<object>(registrations.Count);
        var ownedResources = new List<IDisposable>();
        var ownedResourceSet = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);

        void OwnResource(IDisposable resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (ownedResourceSet.Add(resource))
            {
                ownedResources.Add(resource);
            }
        }

        try
        {
            foreach (var registration in registrations)
            {
                var runtime = registration.CreateRuntime(serviceProvider, OwnResource);
                runtimes.Add(runtime);
                if (runtime is not IDisposable || runtime is not IAsyncDisposable)
                {
                    throw new InvalidOperationException(
                        $"The generated runtime for model '{registration.ModelType}' must support disposal."
                    );
                }

                options.Add((registration.ModelType, registration.OptionsName), runtime);
            }

            foreach (var registration in registrations)
            {
                if (!registration.EnableDynamicOptions)
                {
                    continue;
                }
                if (registries.ContainsKey(registration.ModelType))
                {
                    throw new InvalidOperationException(
                        $"Only one dynamic-options registration is allowed for model '{registration.ModelType}'."
                    );
                }
                var registry = registration.CreateOptionsRegistry(
                    serviceProvider,
                    registrations
                        .Where(candidate => candidate.ModelType == registration.ModelType)
                        .Select(candidate => candidate.OptionsName)
                        .ToArray()
                );
                registries.Add(registration.ModelType, registry);
                runtimes.Add(registry);
                if (registration.EnableProfiles)
                {
                    var manager = registration.CreateProfileManager(
                        registry,
                        registrations
                            .Where(candidate => candidate.ModelType == registration.ModelType)
                            .Select(candidate => candidate.OptionsName)
                            .ToArray(),
                        serviceProvider,
                        OwnResource
                    );
                    profileManagers.Add(registration.ModelType, manager);
                    runtimes.Insert(0, manager);
                }
            }

            return new ConfiglueContext(
                options,
                registries,
                profileManagers,
                runtimes.ToArray(),
                ownedResources.ToArray()
            );
        }
        catch (Exception creationException)
        {
            var errors = new List<Exception>();
            foreach (var runtime in runtimes)
            {
                try
                {
                    if (runtime is IAsyncDisposable asyncDisposable)
                    {
                        // Synchronous construction-failure cleanup or IDisposable boundary; normal source I/O stays asynchronous.
                        asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    else if (runtime is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            foreach (var resource in ownedResources)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            if (errors.Count > 0)
            {
                errors.Insert(0, creationException);
                throw new AggregateException("Context creation and cleanup both failed.", errors);
            }

            throw;
        }
    }
}
