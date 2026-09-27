using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    /// <summary>Gets the generated schemas for the models registered with this builder.</summary>
    /// <remarks>Multiple named registrations of one model can return the same schema more than once.</remarks>
    public IReadOnlyList<ConfiglueModelSchema> ModelSchemas
    {
        get
        {
            var schemas = new ConfiglueModelSchema[_registrations.Count];
            for (var index = 0; index < _registrations.Count; index++)
            {
                schemas[index] = _registrations[index].ModelSchema;
            }

            return Array.AsReadOnly(schemas);
        }
    }

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
    private readonly ConfiglueSourceSetBuilder<TModel> _sources = new();
    private readonly List<IConfiglueValidator<TModel>> _validators = [];
    private readonly List<object> _migrations = [];
    private readonly List<
        Action<string, IServiceProvider?, ConfiglueSourceSetBuilder<TModel>>
    > _sourceConfigurations = [];
    private string _optionsName = string.Empty;
    private StateSource<ConfiglueProfileCatalog>? _profileCatalogSource;
    private string _defaultProfileName = "default";
    private StateWriteRoute _writeRoute;
    private StateWritePlan _writePlan = StateWritePlan.Empty;
    private bool _validateDataAnnotations = true;
    private ReadValidationMode _readValidationMode = ReadValidationMode.EffectiveThrow;
    private bool _enableDynamicOptions;
    private bool _registerAsSingleton;
    private TimeSpan? _onChangeDebounce;
    private ILogger? _logger;
    private Func<TModel, TModel>? _cloneStrategy;
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

    /// <summary>Sets the default write owner for model property paths.</summary>
    /// <remarks>Most-specific paths apply. Operation-level write plans replace routes with matching paths.</remarks>
    public StateWritePlan WritePlan
    {
        get => _writePlan;
        set
        {
            EnsureMutable();
            _writePlan = value ?? throw new ArgumentNullException(nameof(value));
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

    /// <summary>Registers the default model as a DI singleton snapshot for direct injection.</summary>
    /// <remarks>The injected value is created on first resolution and does not follow later source changes. This setting applies only to DI registrations.</remarks>
    public bool RegisterAsSingleton
    {
        get => _registerAsSingleton;
        set
        {
            EnsureMutable();
            _registerAsSingleton = value;
        }
    }

    /// <summary>An optional logger used by this runtime; DI loggers are resolved automatically when omitted.</summary>
    public ILogger? Logger
    {
        get => _logger;
        set
        {
            EnsureMutable();
            _logger = value;
        }
    }

    /// <summary>Sets a custom deep-clone strategy for model values used by this options runtime.</summary>
    /// <remarks>The strategy must return a distinct model with independent mutable members. Full-model saves use it before creating the source fragment; direct fragment patch APIs remain the caller's responsibility.</remarks>
    public void UseCloneStrategy(Func<TModel, TModel> cloneStrategy)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(cloneStrategy);
        _cloneStrategy = cloneStrategy;
    }

    /// <summary>Gets or sets whether DataAnnotations attributes are enforced before writes; enabled by default when dynamic code is supported.</summary>
    public bool ValidateDataAnnotations
    {
        get => _validateDataAnnotations;
        set
        {
            EnsureMutable();
            _validateDataAnnotations = value;
        }
    }

    /// <summary>Gets or sets how validation failures are handled when configuration state is read.</summary>
    public ReadValidationMode ReadValidationMode
    {
        get => _readValidationMode;
        set
        {
            EnsureMutable();
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _readValidationMode = value;
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

    /// <summary>Adds sources shared by non-DI and DI contexts with model-typed provider helpers.</summary>
    public void Sources(Action<ConfiglueSourceSetBuilder<TModel>> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        configure(_sources);
    }

    /// <summary>Adds sources using the service provider available when this model's runtime is created.</summary>
    public void Sources(Action<IServiceProvider?, ConfiglueSourceSetBuilder> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _sourceConfigurations.Add(
            (_, serviceProvider, sources) => configure(serviceProvider, sources)
        );
    }

    /// <summary>Adds sources using DI with model-typed provider helpers.</summary>
    /// <remarks>The callback is recorded during model registration and invoked only after registrations have been added to the service collection. The provider is null in non-DI contexts.</remarks>
    public void Sources(Action<IServiceProvider?, ConfiglueSourceSetBuilder<TModel>> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _sourceConfigurations.Add(
            (_, serviceProvider, sources) => configure(serviceProvider, sources)
        );
    }

    /// <summary>Adds sources whose definitions depend on this named options instance.</summary>
    public void SourcesForOptions(Action<string, ConfiglueSourceSetBuilder> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _sourceConfigurations.Add((optionsName, _, sources) => configure(optionsName, sources));
    }

    /// <summary>Adds sources with the named options instance and model-typed provider helpers.</summary>
    public void SourcesForOptions(Action<string, ConfiglueSourceSetBuilder<TModel>> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _sourceConfigurations.Add((optionsName, _, sources) => configure(optionsName, sources));
    }

    /// <summary>Adds sources using the service provider and named options instance available at runtime creation.</summary>
    public void SourcesForOptions(
        Action<string, IServiceProvider?, ConfiglueSourceSetBuilder> configure
    )
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _sourceConfigurations.Add(
            (optionsName, serviceProvider, sources) =>
                configure(optionsName, serviceProvider, sources)
        );
    }

    /// <summary>Adds sources using DI and the named options instance with model-typed provider helpers.</summary>
    public void SourcesForOptions(
        Action<string, IServiceProvider?, ConfiglueSourceSetBuilder<TModel>> configure
    )
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _sourceConfigurations.Add(configure);
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
        if (_sourceConfigurations.Count == 0)
        {
            return _sources.Build<TFragment>(modelSchema, serviceProvider, ownResource);
        }
        var sources = new ConfiglueSourceSetBuilder<TModel>();
        sources.CopyFrom(_sources);
        foreach (var configure in _sourceConfigurations)
        {
            configure(OptionsName, serviceProvider, sources);
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

    /// <summary>The optional custom clone strategy configured for this model.</summary>
    public Func<TModel, TModel>? CloneStrategy => _cloneStrategy;

    /// <summary>Gets the explicitly configured logger or creates one from the service provider.</summary>
    public ILogger? GetLogger(IServiceProvider? serviceProvider)
    {
        if (_logger is not null)
        {
            return _logger;
        }

        return (
            serviceProvider?.GetService(typeof(ILoggerFactory)) as ILoggerFactory
        )?.CreateLogger($"Configlue.Options.{typeof(TModel).FullName}.{OptionsName}");
    }

    internal ConfiglueModelBuilder<TModel> CloneForOptionsName(string optionsName)
    {
        var clone = new ConfiglueModelBuilder<TModel>
        {
            OptionsName = optionsName,
            WriteRoute = _writeRoute,
            WritePlan = _writePlan,
            ValidateDataAnnotations = _validateDataAnnotations,
            ReadValidationMode = _readValidationMode,
            EnableDynamicOptions = _enableDynamicOptions,
            RegisterAsSingleton = _registerAsSingleton,
            OnChangeDebounce = _onChangeDebounce,
            Logger = _logger,
        };
        clone._sources.CopyFrom(_sources);
        clone._cloneStrategy = _cloneStrategy;
        clone._validators.AddRange(_validators);
        clone._migrations.AddRange(_migrations);
        clone._sourceConfigurations.AddRange(_sourceConfigurations);
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

    /// <summary>Gets the advanced options surface from the process-wide default context.</summary>
    public static IConfiglueOptions<TModel> GetAdvancedOptions<TModel>(string? optionsName = null)
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

        return context.GetAdvancedOptions<TModel>(optionsName);
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
    ConfiglueModelSchema ModelSchema { get; }
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

    public ConfiglueModelSchema ModelSchema => TModel.GetConfiglueSchema();

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
        if (OptionsName.Length == 0)
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

        if (builder.RegisterAsSingleton)
        {
            if (OptionsName.Length != 0)
            {
                throw new InvalidOperationException(
                    "Direct model singleton registration requires the default options name."
                );
            }
            if (typeof(TModel).IsValueType)
            {
                throw new InvalidOperationException(
                    "Direct model singleton registration requires a reference type."
                );
            }

            services.Add(
                ServiceDescriptor.Singleton(
                    typeof(TModel),
                    provider =>
                        provider
                            .GetRequiredService<ConfiglueContext>()
                            .GetOptions<TModel>(OptionsName)
                            .GetValueAsync()
                            .AsTask()
                            .GetAwaiter()
                            .GetResult()
                )
            );
        }
    }

    public void Accept(IConfiglueRegistrationVisitor visitor) => visitor.Visit(this);
}
