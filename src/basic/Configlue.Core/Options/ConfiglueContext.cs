using Configlue.Resources;

namespace Configlue;

/// <summary>An isolated, disposable set of registered configuration states.</summary>
public sealed class ConfiglueContext : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<(Type ModelType, string Name), object> _states;
    private readonly Dictionary<Type, object> _registries;
    private readonly Dictionary<Type, object> _profileManagers;
    private readonly object[] _runtimes;
    private readonly object[] _ownedResources;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;

    internal IReadOnlyList<object> OwnedResourcesForTests => _ownedResources;

    private ConfiglueContext(
        Dictionary<(Type ModelType, string Name), object> states,
        Dictionary<Type, object> registries,
        Dictionary<Type, object> profileManagers,
        object[] runtimes,
        object[] ownedResources
    )
    {
        _states = states;
        _registries = registries;
        _profileManagers = profileManagers;
        _runtimes = runtimes;
        _ownedResources = ownedResources;
    }

    /// <summary>Gets the writable state for a model and optional named instance.</summary>
    public IWritableState<TModel> GetState<TModel>(string? stateName = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = (typeof(TModel), stateName ?? string.Empty);
        if (_states.TryGetValue(key, out var state))
        {
            return (IWritableState<TModel>)state;
        }
        if (
            _registries.TryGetValue(typeof(TModel), out var registry)
            && ((IConfiglueStateRegistry<TModel>)registry).TryGet(key.Item2, out var dynamicState)
            && dynamicState is not null
        )
        {
            return dynamicState;
        }
        throw new KeyNotFoundException(
            $"Model '{typeof(TModel)}' with state name '{key.Item2}' is not registered."
        );
    }

    /// <summary>Reads resolved state and generated provenance details.</summary>
    public IConfiglueInspection<TModel> GetInspection<TModel>(string? stateName = null) =>
        (IConfiglueInspection<TModel>)GetState<TModel>(stateName);

    /// <summary>Gets the explicit arbitrary-subject entry point for a model.</summary>
    public ISubjectState<TModel> GetSubjectState<TModel>(string? stateName = null) =>
        GetState<TModel>(stateName) as ISubjectState<TModel>
        ?? throw new InvalidOperationException(
            $"State for model '{typeof(TModel)}' does not support subject-bound views."
        );

    /// <summary>Opens long-lived drafts of resolved configuration.</summary>
    public IConfiglueEditSessions<TModel> GetEditSessions<TModel>(string? stateName = null) =>
        (IConfiglueEditSessions<TModel>)GetState<TModel>(stateName);

    /// <summary>Reports source topology and background reload failures.</summary>
    public IConfiglueDiagnostics<TModel> GetDiagnostics<TModel>(string? stateName = null) =>
        (IConfiglueDiagnostics<TModel>)GetState<TModel>(stateName);

    /// <summary>Administers source-local writes and source migrations.</summary>
    public IConfiglueSources<TModel> GetSources<TModel>(string? stateName = null) =>
        (IConfiglueSources<TModel>)GetState<TModel>(stateName);

    internal IConfiglueRuntimeState<TModel> GetRuntimeState<TModel>(string? stateName = null) =>
        (IConfiglueRuntimeState<TModel>)GetState<TModel>(stateName);

    /// <summary>Gets the runtime registry for dynamic named states.</summary>
    public IConfiglueStateRegistry<TModel> GetStateRegistry<TModel>()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _registries.TryGetValue(typeof(TModel), out var registry)
            ? (IConfiglueStateRegistry<TModel>)registry
            : throw new InvalidOperationException(
                $"Dynamic named states are not enabled for model '{typeof(TModel)}'."
            );
    }

    /// <summary>Gets the persisted profile manager for a configured model.</summary>
    public IConfiglueProfiledState<TModel> GetProfiledState<TModel>()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _profileManagers.TryGetValue(typeof(TModel), out var manager)
            ? (IConfiglueProfiledState<TModel>)manager
            : throw new InvalidOperationException(
                $"Persisted profiles are not enabled for model '{typeof(TModel)}'."
            );
    }

    /// <summary>Disposes the states and watchers owned by this context.</summary>
    // Synchronous construction-failure cleanup or IDisposable boundary; normal source I/O stays asynchronous.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Asynchronously disposes the states and watchers owned by this context.</summary>
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
                await ConfiglueOwnedResources.DisposeAsync(runtime).ConfigureAwait(false);
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
                await ConfiglueOwnedResources.DisposeAsync(resource).ConfigureAwait(false);
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
        IServiceProvider? serviceProvider,
        IConfiglueHostPaths hostPaths
    )
    {
        var states = new Dictionary<(Type ModelType, string Name), object>();
        var registries = new Dictionary<Type, object>();
        var profileManagers = new Dictionary<Type, object>();
        var runtimes = new List<object>(registrations.Count);
        var ownedResources = new List<object>();
        var ownedResourceSet = new HashSet<object>(ReferenceIdentityComparer.Instance);

        void OwnResource(object resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (resource is not IDisposable && resource is not IAsyncDisposable)
            {
                throw new ArgumentException(
                    "An owned resource must implement IDisposable or IAsyncDisposable.",
                    nameof(resource)
                );
            }

            if (ownedResourceSet.Add(resource))
            {
                ownedResources.Add(resource);
            }
        }

        try
        {
            foreach (var registration in registrations)
            {
                if (registration.RuntimeLifetime == RuntimeLifetimeRequirement.Scoped)
                {
                    if (serviceProvider is null)
                    {
                        throw new InvalidOperationException(
                            $"Model '{registration.ModelType}' requires a scoped runtime because one of its sources consumes scoped services, "
                                + "so it cannot be created in a process-wide Configlue context. Register the model through dependency injection instead."
                        );
                    }

                    // Scoped runtimes are created and owned by each dependency-injection scope.
                    continue;
                }

                var runtime = registration.CreateRuntime(serviceProvider, OwnResource, hostPaths);
                runtimes.Add(runtime);
                if (runtime is not IDisposable || runtime is not IAsyncDisposable)
                {
                    throw new InvalidOperationException(
                        $"The generated runtime for model '{registration.ModelType}' must support disposal."
                    );
                }

                states.Add((registration.ModelType, registration.StateName), runtime);
            }

            foreach (var registration in registrations)
            {
                if (!registration.EnableDynamicStates)
                {
                    continue;
                }
                if (registration.RuntimeLifetime == RuntimeLifetimeRequirement.Scoped)
                {
                    throw new InvalidOperationException(
                        $"Dynamic named states are not supported for model '{registration.ModelType}' because it requires a scoped runtime."
                    );
                }
                if (registries.ContainsKey(registration.ModelType))
                {
                    throw new InvalidOperationException(
                        $"Only one dynamic-state registration is allowed for model '{registration.ModelType}'."
                    );
                }
                var registry = registration.CreateStateRegistry(
                    serviceProvider,
                    registrations
                        .Where(candidate => candidate.ModelType == registration.ModelType)
                        .Select(candidate => candidate.StateName)
                        .ToArray(),
                    hostPaths
                );
                registries.Add(registration.ModelType, registry);
                runtimes.Add(registry);
                if (registration.EnableProfiles)
                {
                    var manager = registration.CreateProfileManager(
                        registry,
                        registrations
                            .Where(candidate => candidate.ModelType == registration.ModelType)
                            .Select(candidate => candidate.StateName)
                            .ToArray(),
                        serviceProvider,
                        OwnResource
                    );
                    profileManagers.Add(registration.ModelType, manager);
                    runtimes.Insert(0, manager);
                }
            }

            return new ConfiglueContext(
                states,
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
                    ConfiglueOwnedResources.Dispose(runtime);
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
                    ConfiglueOwnedResources.Dispose(resource);
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
