using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue;

/// <summary>Collects shared model and source definitions for a Configlue context.</summary>
public sealed class ConfiglueBuilder
{
    private readonly List<IConfiglueModelRegistration> _registrations = [];
    private bool _sealed;

    /// <summary>Adds one model registration.</summary>
    public void Add<TModel>(Action<ConfiglueModelBuilder<TModel>> configure)
        where TModel : IConfiglueFacadeModel<TModel>
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        var model = new ConfiglueModelBuilder<TModel>();
        configure(model);
        model.Seal();

        if (
            _registrations.Any(registration =>
                registration.ModelType == typeof(TModel)
                && string.Equals(
                    registration.OptionsName,
                    model.OptionsName,
                    StringComparison.Ordinal
                )
            )
        )
        {
            throw new ArgumentException(
                $"Model '{typeof(TModel)}' with options name '{model.OptionsName}' is already registered.",
                nameof(configure)
            );
        }

        _registrations.Add(new ConfiglueModelRegistration<TModel>(model));
    }

    /// <summary>Builds an independent context from the collected definitions.</summary>
    public ConfiglueContext CreateContext(IServiceProvider? serviceProvider = null) =>
        ConfiglueContext.Create(_registrations, serviceProvider);

    internal IReadOnlyList<IConfiglueModelRegistration> Registrations => _registrations;

    internal void Seal() => _sealed = true;

    private void EnsureMutable()
    {
        if (_sealed)
        {
            throw new InvalidOperationException("The Configlue builder has already been consumed.");
        }
    }
}

/// <summary>Configures the sources and runtime behavior for one generated model.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
public sealed class ConfiglueModelBuilder<TModel>
    where TModel : IConfiglueFacadeModel<TModel>
{
    private readonly ConfiglueSourceSetBuilder _sources = new();
    private readonly List<IConfiglueValidator<TModel>> _validators = [];
    private readonly List<object> _migrations = [];
    private readonly List<Action<string, ConfiglueSourceSetBuilder>> _namedSourceConfigurations =
    [];
    private string _optionsName = Options.DefaultName;
    private StateSource<ConfiglueProfileCatalog>? _profileCatalogSource;
    private string _defaultProfileName = "default";
    private StateWriteRoute _writeRoute;
    private bool _validateDataAnnotations;
    private bool _enableDynamicOptions;
    private TimeSpan? _onChangeDebounce;
    private bool _sealed;

    /// <summary>The name used by named options and profiles. The default is the unnamed instance.</summary>
    public string OptionsName
    {
        get => _optionsName;
        set
        {
            EnsureMutable();
            _optionsName = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    /// <summary>Selects the logical source used for ordinary writes.</summary>
    public StateWriteRoute WriteRoute
    {
        get => _writeRoute;
        set
        {
            EnsureMutable();
            _writeRoute = value;
        }
    }

    /// <summary>Enables runtime registration of named instances for this model.</summary>
    public bool EnableDynamicOptions
    {
        get => _enableDynamicOptions;
        set
        {
            EnsureMutable();
            _enableDynamicOptions = value;
        }
    }

    /// <summary>Enables validation attributes on the model before writes.</summary>
    public bool ValidateDataAnnotations
    {
        get => _validateDataAnnotations;
        set
        {
            EnsureMutable();
            _validateDataAnnotations = value;
        }
    }

    /// <summary>Sets the delay between a source change and a watcher notification.</summary>
    public TimeSpan? OnChangeDebounce
    {
        get => _onChangeDebounce;
        set
        {
            EnsureMutable();
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _onChangeDebounce = value;
        }
    }

    /// <summary>Adds sources shared by non-DI and DI contexts.</summary>
    public void Sources(Action<ConfiglueSourceSetBuilder> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        configure(_sources);
    }

    /// <summary>Adds sources whose definitions depend on this named options instance.</summary>
    public void SourcesForOptions(Action<string, ConfiglueSourceSetBuilder> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _namedSourceConfigurations.Add(configure);
    }

    /// <summary>Enables a persisted profile catalog backed by a writable state source.</summary>
    public void EnableProfiles(
        StateSource<ConfiglueProfileCatalog> catalogSource,
        string defaultProfileName = "default"
    )
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(catalogSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProfileName);
        if (catalogSource.Writer is null)
        {
            throw new ArgumentException(
                "The profile catalog source must support writes.",
                nameof(catalogSource)
            );
        }
        _profileCatalogSource = catalogSource;
        _defaultProfileName = defaultProfileName;
        _enableDynamicOptions = true;
    }

    /// <summary>Adds a validator for this model.</summary>
    public void AddValidator(IConfiglueValidator<TModel> validator)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(validator);
        _validators.Add(validator);
    }

    /// <summary>Adds a schema migration for the model's generated fragment.</summary>
    public void AddMigration<TFragment>(IStateSchemaMigration<TFragment> migration)
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(migration);
        _migrations.Add(migration);
    }

    /// <summary>Builds the typed source set using the generated model's closed fragment type.</summary>
    public StateSourceSet<TFragment> BuildSources<TFragment>(IServiceProvider? serviceProvider)
        where TFragment : class, IConfiglueFragment<TFragment> =>
        BuildSources<TFragment>(default!, serviceProvider, static _ => { });

    /// <summary>Builds the source set and reports resources created by helper definitions.</summary>
    public StateSourceSet<TFragment> BuildSources<TFragment>(
        IServiceProvider? serviceProvider,
        Action<IDisposable> ownResource
    )
        where TFragment : class, IConfiglueFragment<TFragment> =>
        BuildSources<TFragment>(default!, serviceProvider, ownResource);

    /// <summary>Builds the source set and reports resources created by helper definitions.</summary>
    public StateSourceSet<TFragment> BuildSources<TFragment>(
        ConfiglueModelSchema modelSchema,
        IServiceProvider? serviceProvider,
        Action<IDisposable> ownResource
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(ownResource);
        if (_namedSourceConfigurations.Count == 0)
        {
            return _sources.Build<TFragment>(modelSchema, serviceProvider, ownResource);
        }
        var sources = new ConfiglueSourceSetBuilder();
        sources.CopyFrom(_sources);
        foreach (var configure in _namedSourceConfigurations)
        {
            configure(OptionsName, sources);
        }
        return sources.Build<TFragment>(modelSchema, serviceProvider, ownResource);
    }

    /// <summary>Gets explicit and dependency-injected migrations for the generated fragment type.</summary>
    public IReadOnlyList<IStateSchemaMigration<TFragment>> GetMigrations<TFragment>(
        IServiceProvider? serviceProvider
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        var mismatchedMigration = _migrations.FirstOrDefault(migration =>
            migration is not IStateSchemaMigration<TFragment>
        );
        if (mismatchedMigration is not null)
        {
            throw new InvalidOperationException(
                $"A schema migration for model '{typeof(TModel)}' uses a fragment type that does not match '{typeof(TFragment)}'."
            );
        }

        var migrations = _migrations.Cast<IStateSchemaMigration<TFragment>>().ToList();
        if (serviceProvider is not null)
        {
            migrations.AddRange(serviceProvider.GetServices<IStateSchemaMigration<TFragment>>());
        }

        return migrations;
    }

    /// <summary>Gets explicit and dependency-injected validators for this model.</summary>
    public IReadOnlyList<IConfiglueValidator<TModel>> GetValidators(
        IServiceProvider? serviceProvider
    )
    {
        var validators = new List<IConfiglueValidator<TModel>>(_validators);
        if (serviceProvider is not null)
        {
            validators.AddRange(serviceProvider.GetServices<IConfiglueValidator<TModel>>());
        }

        return validators;
    }

    internal StateSource<ConfiglueProfileCatalog>? ProfileCatalogSource => _profileCatalogSource;
    internal string DefaultProfileName => _defaultProfileName;

    internal ConfiglueModelBuilder<TModel> CloneForOptionsName(string optionsName)
    {
        var clone = new ConfiglueModelBuilder<TModel>
        {
            OptionsName = optionsName,
            WriteRoute = _writeRoute,
            ValidateDataAnnotations = _validateDataAnnotations,
            EnableDynamicOptions = _enableDynamicOptions,
            OnChangeDebounce = _onChangeDebounce,
        };
        clone._sources.CopyFrom(_sources);
        clone._validators.AddRange(_validators);
        clone._migrations.AddRange(_migrations);
        clone._namedSourceConfigurations.AddRange(_namedSourceConfigurations);
        clone._profileCatalogSource = _profileCatalogSource;
        clone._defaultProfileName = _defaultProfileName;
        return clone;
    }

    internal void Seal()
    {
        _sealed = true;
        _sources.Seal();
    }

    private void EnsureMutable()
    {
        if (_sealed)
        {
            throw new InvalidOperationException("The model registration has already been added.");
        }
    }
}

/// <summary>Collects typed state sources without requiring a Fragment type argument on the model API.</summary>
public sealed class ConfiglueSourceSetBuilder
{
    private readonly List<IConfiglueSourceRegistration> _sources = [];
    private bool _sealed;

    /// <summary>Adds an already-created source. Its resource instances remain caller-owned.</summary>
    public void Add<TFragment>(StateSource<TFragment> source)
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(source);
        _sources.Add(new ConfiglueSourceRegistration<TFragment>(_ => source));
    }

    /// <summary>Adds a source factory. The service provider is null in a non-DI context.</summary>
    public void Add<TFragment>(Func<IServiceProvider?, StateSource<TFragment>> sourceFactory)
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(sourceFactory);
        _sources.Add(new ConfiglueSourceRegistration<TFragment>(sourceFactory));
    }

    /// <summary>Adds a provider-defined source using the generated model's fragment type.</summary>
    public void Add(IConfiglueSourceDefinition definition)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(definition);
        _sources.Add(new ConfiglueSourceDefinitionRegistration(definition));
    }

    internal StateSourceSet<TFragment> Build<TFragment>(IServiceProvider? serviceProvider)
        where TFragment : class, IConfiglueFragment<TFragment> =>
        Build<TFragment>(default!, serviceProvider, static _ => { });

    internal StateSourceSet<TFragment> Build<TFragment>(
        ConfiglueModelSchema? modelSchema,
        IServiceProvider? serviceProvider,
        Action<IDisposable> ownResource
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        if (modelSchema is null && _sources.Any(static source => source.RequiresGeneratedModel))
        {
            throw new InvalidOperationException(
                "Provider source definitions require the generated facade to supply model metadata and resource ownership."
            );
        }

        var sources = new List<StateSource<TFragment>>(_sources.Count);
        foreach (var registration in _sources)
        {
            sources.Add(registration.Create<TFragment>(modelSchema, serviceProvider, ownResource));
        }

        return new StateSourceSet<TFragment>(sources);
    }

    internal void CopyFrom(ConfiglueSourceSetBuilder source)
    {
        if (_sealed)
            throw new InvalidOperationException("The source registration has already been added.");
        _sources.AddRange(source._sources);
    }

    internal void Seal() => _sealed = true;

    private void EnsureMutable()
    {
        if (_sealed)
        {
            throw new InvalidOperationException("The source registration has already been added.");
        }
    }

    private interface IConfiglueSourceRegistration
    {
        bool RequiresGeneratedModel { get; }

        StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema? modelSchema,
            IServiceProvider? serviceProvider,
            Action<IDisposable> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment>;
    }

    private sealed class ConfiglueSourceRegistration<TFragment>(
        Func<IServiceProvider?, StateSource<TFragment>> sourceFactory
    ) : IConfiglueSourceRegistration
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        public bool RequiresGeneratedModel => false;

        public StateSource<TRequestedFragment> Create<TRequestedFragment>(
            ConfiglueModelSchema? modelSchema,
            IServiceProvider? serviceProvider,
            Action<IDisposable> ownResource
        )
            where TRequestedFragment : class, IConfiglueFragment<TRequestedFragment>
        {
            if (typeof(TFragment) != typeof(TRequestedFragment))
            {
                throw new InvalidOperationException(
                    $"A source for model fragment '{typeof(TFragment)}' cannot be used with '{typeof(TRequestedFragment)}'."
                );
            }

            return (StateSource<TRequestedFragment>)
                (object)(
                    sourceFactory(serviceProvider)
                    ?? throw new InvalidOperationException("A source factory returned null.")
                );
        }
    }

    private sealed class ConfiglueSourceDefinitionRegistration(
        IConfiglueSourceDefinition definition
    ) : IConfiglueSourceRegistration
    {
        public bool RequiresGeneratedModel => true;

        public StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema? modelSchema,
            IServiceProvider? serviceProvider,
            Action<IDisposable> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment> =>
            definition.Create<TFragment>(
                modelSchema
                    ?? throw new InvalidOperationException(
                        "Provider source definitions require generated model metadata."
                    ),
                serviceProvider,
                ownResource
            ) ?? throw new InvalidOperationException("A source definition returned null.");
    }
}

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
        var key = (typeof(TModel), optionsName ?? Options.DefaultName);
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
                    continue;
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
                            .ToArray()
                    );
                    profileManagers.Add(registration.ModelType, manager);
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

/// <summary>Provides process-wide convenience access to one default Configlue context.</summary>
public static class Configlue
{
    private static readonly object Gate = new();
    private static ConfiglueContext? _defaultContext;

    /// <summary>Initializes the process-wide default context.</summary>
    public static void Initialize(Action<ConfiglueBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ConfiglueBuilder();
        configure(builder);
        builder.Seal();
        var context = builder.CreateContext();

        lock (Gate)
        {
            if (_defaultContext is not null)
            {
                context.Dispose();
                throw new InvalidOperationException(
                    "The process-wide Configlue context has already been initialized."
                );
            }

            _defaultContext = context;
        }
    }

    /// <summary>Creates an independent, lifetime-managed context.</summary>
    public static ConfiglueContext CreateContext(Action<ConfiglueBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ConfiglueBuilder();
        configure(builder);
        builder.Seal();
        return builder.CreateContext();
    }

    /// <summary>Gets options from the initialized process-wide context.</summary>
    public static IWritableOptions<TModel> GetOptions<TModel>(string? optionsName = null)
    {
        ConfiglueContext context;
        lock (Gate)
        {
            context =
                _defaultContext
                ?? throw new InvalidOperationException(
                    "The process-wide Configlue context has not been initialized."
                );
        }

        return context.GetOptions<TModel>(optionsName);
    }

    /// <summary>Disposes the initialized process-wide context and clears it for later initialization.</summary>
    public static async ValueTask ShutdownAsync()
    {
        ConfiglueContext? context;
        lock (Gate)
        {
            context = _defaultContext;
            _defaultContext = null;
        }

        if (context is not null)
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal interface IConfiglueModelRegistration
{
    Type ModelType { get; }
    string OptionsName { get; }
    bool EnableDynamicOptions { get; }
    bool EnableProfiles { get; }
    object CreateRuntime(IServiceProvider? serviceProvider, Action<IDisposable> ownResource);
    object CreateOptionsRegistry(
        IServiceProvider? serviceProvider,
        IReadOnlyList<string> reservedNames
    );
    object CreateProfileManager(object registry, IReadOnlyList<string> reservedNames);
    void AddServiceDescriptors(IServiceCollection services);
    void Accept(IConfiglueRegistrationVisitor visitor);
}

internal interface IConfiglueRegistrationVisitor
{
    void Visit<TModel>(ConfiglueModelRegistration<TModel> registration)
        where TModel : IConfiglueFacadeModel<TModel>;
}

internal sealed class ConfiglueModelRegistration<TModel>(ConfiglueModelBuilder<TModel> builder)
    : IConfiglueModelRegistration
    where TModel : IConfiglueFacadeModel<TModel>
{
    public Type ModelType => typeof(TModel);

    public string OptionsName => builder.OptionsName;

    public bool EnableDynamicOptions => builder.EnableDynamicOptions;

    public bool EnableProfiles => builder.ProfileCatalogSource is not null;

    public object CreateRuntime(
        IServiceProvider? serviceProvider,
        Action<IDisposable> ownResource
    ) => TModel.CreateConfiglueRuntime(builder, serviceProvider, ownResource);

    public object CreateProfileManager(object registry, IReadOnlyList<string> reservedNames)
    {
        var catalogSource =
            builder.ProfileCatalogSource
            ?? throw new InvalidOperationException(
                "Profile support was not enabled for this registration."
            );
        if (reservedNames.Contains(builder.DefaultProfileName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Default profile '{builder.DefaultProfileName}' conflicts with a fixed OptionsName."
            );
        }
        return TModel.CreateConfiglueProfileManager(
            (IConfiglueOptionsRegistry<TModel>)registry,
            catalogSource,
            builder.DefaultProfileName
        );
    }

    public object CreateOptionsRegistry(
        IServiceProvider? serviceProvider,
        IReadOnlyList<string> reservedNames
    ) =>
        new ConfiglueFacadeOptionsRegistry<TModel>(
            name =>
            {
                var resources = new List<IDisposable>();
                var resourceSet = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);
                IWritableOptions<TModel>? runtime = null;
                try
                {
                    var dynamicBuilder = builder.CloneForOptionsName(name);
                    runtime = TModel.CreateConfiglueRuntime(
                        dynamicBuilder,
                        serviceProvider,
                        resource =>
                        {
                            ArgumentNullException.ThrowIfNull(resource);
                            if (resourceSet.Add(resource))
                                resources.Add(resource);
                        }
                    );
                    if (runtime is not IDisposable || runtime is not IAsyncDisposable)
                        throw new InvalidOperationException(
                            $"The generated runtime for model '{typeof(TModel)}' must support disposal."
                        );
                    return (runtime, resources.ToArray());
                }
                catch (Exception creationException)
                {
                    List<Exception>? cleanupErrors = null;
                    try
                    {
                        if (runtime is IAsyncDisposable asyncDisposable)
                            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        else if (runtime is IDisposable disposable)
                            disposable.Dispose();
                    }
                    catch (Exception cleanupException)
                    {
                        (cleanupErrors ??= []).Add(cleanupException);
                    }
                    foreach (var resource in resources)
                    {
                        try
                        {
                            resource.Dispose();
                        }
                        catch (Exception cleanupException)
                        {
                            (cleanupErrors ??= []).Add(cleanupException);
                        }
                    }
                    if (cleanupErrors is not null)
                    {
                        cleanupErrors.Insert(0, creationException);
                        throw new AggregateException(
                            "Dynamic runtime creation and cleanup both failed.",
                            cleanupErrors
                        );
                    }
                    throw;
                }
            },
            reservedNames
        );

    public void AddServiceDescriptors(IServiceCollection services)
    {
        if (EnableDynamicOptions)
        {
            services.AddSingleton<IConfiglueOptionsRegistry<TModel>>(provider =>
                provider.GetRequiredService<ConfiglueContext>().GetOptionsRegistry<TModel>()
            );
        }
        if (EnableProfiles)
        {
            services.AddSingleton<IConfiglueProfiledOptions<TModel>>(provider =>
                provider.GetRequiredService<ConfiglueContext>().GetProfiledOptions<TModel>()
            );
        }
        if (OptionsName == Options.DefaultName)
        {
            services.AddSingleton<IReadOnlyOptions<TModel>>(provider =>
                provider.GetRequiredService<ConfiglueContext>().GetOptions<TModel>(OptionsName)
            );
            services.AddSingleton<IWritableOptions<TModel>>(provider =>
                provider.GetRequiredService<ConfiglueContext>().GetOptions<TModel>(OptionsName)
            );
        }
        else
        {
            services.AddKeyedSingleton<IReadOnlyOptions<TModel>>(
                OptionsName,
                (provider, _) =>
                    provider.GetRequiredService<ConfiglueContext>().GetOptions<TModel>(OptionsName)
            );
            services.AddKeyedSingleton<IWritableOptions<TModel>>(
                OptionsName,
                (provider, _) =>
                    provider.GetRequiredService<ConfiglueContext>().GetOptions<TModel>(OptionsName)
            );
        }
    }

    public void Accept(IConfiglueRegistrationVisitor visitor) => visitor.Visit(this);
}
