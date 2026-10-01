using Configlue.CompilerServices;
using Configlue.Resources;
using Microsoft.Extensions.Logging;

namespace Configlue;

/// <summary>Collects shared model and source definitions for a Configlue context.</summary>
public sealed class ConfiglueBuilder
{
    private readonly List<IConfiglueModelRegistration> _registrations = [];
    private IConfiglueHostPaths _hostPaths = ConfiglueHostPathProfile.Default;
    private bool _sealed;

    /// <summary>Selects the host profile used to resolve standard storage locations.</summary>
    public ConfiglueBuilder UseHostPaths(IConfiglueHostPaths hostPaths)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(hostPaths);
        _hostPaths = hostPaths;
        return this;
    }

    /// <summary>Overrides one standard location while retaining the selected host profile for other locations.</summary>
    public ConfiglueBuilder OverrideHostPath(
        ConfiglueStandardLocation location,
        Func<string, string?> resolver
    )
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(resolver);
        var profile =
            _hostPaths as ConfiglueHostPathProfile ?? ConfiglueHostPathProfile.From(_hostPaths);
        _hostPaths = profile.WithOverride(location, resolver);
        return this;
    }

    /// <summary>Gets the host path profile selected for this builder.</summary>
    public IConfiglueHostPaths HostPaths => _hostPaths;

    /// <summary>Resolves one standard host directory using the configured host path profile.</summary>
    public string ResolveStandardDirectory(
        ConfiglueStandardLocation location,
        string applicationId = ""
    ) => ConfiglueStandardPaths.ResolveDirectory(_hostPaths, location, applicationId);

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
                && string.Equals(registration.StateName, model.StateName, StringComparison.Ordinal)
            )
        )
        {
            throw new ArgumentException(
                $"Model '{typeof(TModel)}' with state name '{model.StateName}' is already registered.",
                nameof(configure)
            );
        }

        _registrations.Add(new ConfiglueModelRegistration<TModel>(model));
    }

    /// <summary>Builds an independent context from the collected definitions.</summary>
    public ConfiglueContext CreateContext(IServiceProvider? serviceProvider = null) =>
        ConfiglueContext.Create(_registrations, serviceProvider, _hostPaths);

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
    internal IConfiglueHostPaths HostPaths { get; set; } = ConfiglueHostPathProfile.Default;
    private readonly ConfiglueSourceSetBuilder<TModel> _sources = new();
    private readonly List<IConfiglueValidator<TModel>> _validators = [];
    private readonly List<object> _migrations = [];
    private readonly List<
        Action<string, IServiceProvider?, ConfiglueSourceSetBuilder<TModel>>
    > _sourceConfigurations = [];
    private string _stateName = string.Empty;
    private StateSource<ConfiglueProfileCatalog>? _profileCatalogSource;
    private Func<
        IServiceProvider?,
        Action<object>,
        StateSource<ConfiglueProfileCatalog>
    >? _profileCatalogSourceFactory;
    private string _defaultProfileName = "default";
    private StateWritePlan _writePlan = StateWritePlan.Empty;
    private bool _validateDataAnnotations = true;
    private ReadValidationMode _readValidationMode = ReadValidationMode.EffectiveThrow;
    private WriteConflictResolution _writeConflictResolution =
        WriteConflictResolution.FailOnConflict;
    private bool _enableDynamicStates;
    private TimeSpan? _onChangeDebounce;
    private ILogger? _logger;
    private Func<TModel, TModel>? _cloneStrategy;
    private Type? _subjectAccessorType;
    private RuntimeLifetimeRequirement? _runtimeLifetime;
    private bool _sealed;

    /// <summary>The name used by state instances and profiles. The default is the unnamed instance.</summary>
    public string StateName
    {
        get => _stateName;
        set
        {
            EnsureMutable();
            _stateName = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    /// <summary>Sets the write ownership plan for ordinary edits.</summary>
    /// <remarks>Most-specific paths apply. Operation-level write plans replace ownership with matching paths.</remarks>
    public StateWritePlan WritePlan
    {
        get => _writePlan;
        set
        {
            EnsureMutable();
            _writePlan = value ?? throw new ArgumentNullException(nameof(value));
        }
    }

    /// <summary>Configures deterministic write ownership for ordinary edits.</summary>
    /// <example>
    /// <code>
    /// model.Writes(write =>
    /// {
    ///     write.DefaultTo(userSource);
    ///     write.Route(x => x.Database, databaseSource);
    /// });
    /// </code>
    /// </example>
    public void Writes(Action<StateWritePlanBuilder<TModel>> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new StateWritePlanBuilder<TModel>();
        configure(builder);
        _writePlan = builder.Build();
    }

    /// <summary>Enables runtime registration of named instances for this model.</summary>
    public bool EnableDynamicStates
    {
        get => _enableDynamicStates;
        set
        {
            EnsureMutable();
            _enableDynamicStates = value;
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

    /// <summary>Sets a custom deep-clone strategy for model values used by this state runtime.</summary>
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

    /// <summary>Controls how edits are rebased when values change concurrently.</summary>
    public WriteConflictResolution WriteConflictResolution
    {
        get => _writeConflictResolution;
        set
        {
            EnsureMutable();
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _writeConflictResolution = value;
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

    /// <summary>Adds sources with strongly typed provider helpers during registration.</summary>
    public void Sources(Action<ConfiglueSourceSetBuilder<TModel>> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        configure(_sources);
    }

    /// <summary>Resolves the current subject from a scoped dependency-injection accessor.</summary>
    /// <remarks>
    /// The accessor type must be registered with the application's service provider. State
    /// interfaces injected into a scope become scoped views; the underlying runtime remains shared.
    /// </remarks>
    public void PerSubject<TAccessor>()
        where TAccessor : class, IConfiglueSubjectAccessor
    {
        EnsureMutable();
        _subjectAccessorType = typeof(TAccessor);
    }

    /// <summary>Overrides the runtime lifetime inferred from the configured sources.</summary>
    /// <remarks>
    /// Most models infer their lifetime from their sources. Use this escape hatch only when a
    /// custom source or resource cannot declare its own requirement through
    /// <see cref="ConfiglueSourceRegistration.RuntimeLifetime"/> or
    /// <see cref="IConfiglueRuntimeLifetimeSource"/>.
    /// </remarks>
    public RuntimeLifetimeRequirement RuntimeLifetime
    {
        get => _runtimeLifetime ?? RuntimeLifetimeRequirement.Shared;
        set
        {
            EnsureMutable();
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _runtimeLifetime = value;
        }
    }

    /// <summary>Requires this model's runtime to be created per dependency-injection scope.</summary>
    public void UseScopedRuntime() => RuntimeLifetime = RuntimeLifetimeRequirement.Scoped;

    /// <summary>Keeps this model's runtime shared across dependency-injection scopes.</summary>
    public void UseSharedRuntime() => RuntimeLifetime = RuntimeLifetimeRequirement.Shared;

    /// <summary>Configures sources when the runtime is created, with its name and application services.</summary>
    public void ConfigureSources(Action<ConfiglueSourceRegistrationContext<TModel>> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        _sourceConfigurations.Add(
            (name, services, sources) =>
                configure(new ConfiglueSourceRegistrationContext<TModel>(name, services, sources))
        );
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
        _profileCatalogSourceFactory = null;
        _defaultProfileName = defaultProfileName;
        _enableDynamicStates = true;
    }

    /// <summary>Enables persisted profiles using a catalog source created for each context.</summary>
    /// <remarks>
    /// Resources created by the factory must be reported through its resource ownership callback.
    /// Caller-supplied resources remain caller-owned.
    /// </remarks>
    public void EnableProfiles(
        Func<
            IServiceProvider?,
            Action<object>,
            StateSource<ConfiglueProfileCatalog>
        > catalogSourceFactory,
        string defaultProfileName = "default"
    )
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(catalogSourceFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProfileName);
        _profileCatalogSource = null;
        _profileCatalogSourceFactory = catalogSourceFactory;
        _defaultProfileName = defaultProfileName;
        _enableDynamicStates = true;
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
    internal StateSourceSet<TFragment> BuildSources<TFragment>(IServiceProvider? serviceProvider)
        where TFragment : class, IConfiglueFragment<TFragment> =>
        BuildSources<TFragment>(default!, serviceProvider, static _ => { });

    /// <summary>Builds the source set and reports resources created by helper definitions.</summary>
    internal StateSourceSet<TFragment> BuildSources<TFragment>(
        IServiceProvider? serviceProvider,
        Action<object> ownResource
    )
        where TFragment : class, IConfiglueFragment<TFragment> =>
        BuildSources<TFragment>(default!, serviceProvider, ownResource);

    /// <summary>Builds the source set and reports resources created by helper definitions.</summary>
    internal StateSourceSet<TFragment> BuildSources<TFragment>(
        ConfiglueModelSchema modelSchema,
        IServiceProvider? serviceProvider,
        Action<object> ownResource
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(ownResource);
        if (_sourceConfigurations.Count == 0)
        {
            return _sources.Build<TFragment>(modelSchema, serviceProvider, ownResource, HostPaths);
        }
        var sources = new ConfiglueSourceSetBuilder<TModel>();
        sources.CopyFrom(_sources);
        foreach (var configure in _sourceConfigurations)
        {
            configure(StateName, serviceProvider, sources);
        }
        return sources.Build<TFragment>(modelSchema, serviceProvider, ownResource, HostPaths);
    }

    /// <summary>Gets explicit and dependency-injected migrations for the generated fragment type.</summary>
    internal IReadOnlyList<IStateSchemaMigration<TFragment>> GetMigrations<TFragment>(
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
        migrations.AddRange(GetServices<IStateSchemaMigration<TFragment>>(serviceProvider));
        return migrations;
    }

    private static IEnumerable<TService> GetServices<TService>(IServiceProvider? serviceProvider) =>
        serviceProvider?.GetService(typeof(IEnumerable<TService>)) as IEnumerable<TService> ?? [];

    /// <summary>Gets explicit and dependency-injected validators for this model.</summary>
    internal IReadOnlyList<IConfiglueValidator<TModel>> GetValidators(
        IServiceProvider? serviceProvider
    )
    {
        var validators = new List<IConfiglueValidator<TModel>>(_validators);
        validators.AddRange(GetServices<IConfiglueValidator<TModel>>(serviceProvider));
        return validators;
    }

    internal bool HasProfileCatalog =>
        _profileCatalogSource is not null || _profileCatalogSourceFactory is not null;

    internal StateSource<ConfiglueProfileCatalog>? ProfileCatalogSource => _profileCatalogSource;

    internal Func<
        IServiceProvider?,
        Action<object>,
        StateSource<ConfiglueProfileCatalog>
    >? ProfileCatalogSourceFactory => _profileCatalogSourceFactory;

    internal string DefaultProfileName => _defaultProfileName;

    /// <summary>The optional custom clone strategy configured for this model.</summary>
    internal Func<TModel, TModel>? CloneStrategy => _cloneStrategy;

    internal Type? SubjectAccessorType => _subjectAccessorType;

    /// <summary>The runtime lifetime inferred from explicit configuration or the source topology.</summary>
    internal RuntimeLifetimeRequirement EffectiveRuntimeLifetime =>
        _runtimeLifetime ?? _sources.DeclaredRuntimeLifetime;

    /// <summary>Gets the explicitly configured logger or creates one from the service provider.</summary>
    internal ILogger? GetLogger(IServiceProvider? serviceProvider)
    {
        if (_logger is not null)
        {
            return _logger;
        }

        return (
            serviceProvider?.GetService(typeof(ILoggerFactory)) as ILoggerFactory
        )?.CreateLogger($"Configlue.State.{typeof(TModel).FullName}.{StateName}");
    }

    internal ConfiglueModelBuilder<TModel> CloneForStateName(string stateName)
    {
        var clone = new ConfiglueModelBuilder<TModel>
        {
            StateName = stateName,
            WritePlan = _writePlan,
            ValidateDataAnnotations = _validateDataAnnotations,
            ReadValidationMode = _readValidationMode,
            WriteConflictResolution = _writeConflictResolution,
            EnableDynamicStates = _enableDynamicStates,
            OnChangeDebounce = _onChangeDebounce,
            Logger = _logger,
            _subjectAccessorType = _subjectAccessorType,
            _runtimeLifetime = _runtimeLifetime,
            HostPaths = HostPaths,
        };
        clone._sources.CopyFrom(_sources);
        clone._cloneStrategy = _cloneStrategy;
        clone._validators.AddRange(_validators);
        clone._migrations.AddRange(_migrations);
        clone._sourceConfigurations.AddRange(_sourceConfigurations);
        clone._profileCatalogSource = _profileCatalogSource;
        clone._profileCatalogSourceFactory = _profileCatalogSourceFactory;
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

internal interface IConfiglueModelRegistration
{
    Type ModelType { get; }
    ConfiglueModelSchema ModelSchema { get; }
    string StateName { get; }
    bool IsPerSubject { get; }
    Type? SubjectAccessorType { get; }
    bool EnableDynamicStates { get; }
    bool EnableProfiles { get; }
    RuntimeLifetimeRequirement RuntimeLifetime { get; }
    object CreateRuntime(
        IServiceProvider? serviceProvider,
        Action<object> ownResource,
        IConfiglueHostPaths hostPaths
    );
    object CreateStateRegistry(
        IServiceProvider? serviceProvider,
        IReadOnlyList<string> reservedNames,
        IConfiglueHostPaths hostPaths
    );
    object CreateProfileManager(
        object registry,
        IReadOnlyList<string> reservedNames,
        IServiceProvider? serviceProvider,
        Action<object> ownResource
    );
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

    public ConfiglueModelSchema ModelSchema => ConfiglueModelDescriptor<TModel>.Current.Schema;

    public string StateName => builder.StateName;

    public bool IsPerSubject => builder.SubjectAccessorType is not null;

    public Type? SubjectAccessorType => builder.SubjectAccessorType;

    public bool EnableDynamicStates => builder.EnableDynamicStates;

    public bool EnableProfiles => builder.HasProfileCatalog;

    public RuntimeLifetimeRequirement RuntimeLifetime => builder.EffectiveRuntimeLifetime;

    public object CreateRuntime(
        IServiceProvider? serviceProvider,
        Action<object> ownResource,
        IConfiglueHostPaths hostPaths
    )
    {
        builder.HostPaths = hostPaths;
        var runtime = ConfiglueModelDescriptor<TModel>.Current.CreateRuntime(
            builder,
            serviceProvider,
            ownResource
        );
        if (
            RuntimeLifetime != RuntimeLifetimeRequirement.Scoped
            && runtime is IConfiglueRuntimeLifetimeProvider lifetimeProvider
            && lifetimeProvider.RuntimeLifetime == RuntimeLifetimeRequirement.Scoped
        )
        {
            throw new InvalidOperationException(
                $"Model '{typeof(TModel)}' is registered with a shared runtime but one of its sources requires a scoped runtime. "
                    + "Register the scoped source through the dependency-injection model registration path, or mark the model scoped."
            );
        }

        return runtime;
    }

    public object CreateProfileManager(
        object registry,
        IReadOnlyList<string> reservedNames,
        IServiceProvider? serviceProvider,
        Action<object> ownResource
    )
    {
        var catalogSource =
            builder.ProfileCatalogSource
            ?? builder.ProfileCatalogSourceFactory?.Invoke(serviceProvider, ownResource)
            ?? throw new InvalidOperationException(
                "Profile support was not enabled for this registration."
            );
        if (catalogSource.Writer is null)
        {
            throw new InvalidOperationException(
                "The profile catalog source factory returned a source that does not support writes."
            );
        }
        if (reservedNames.Contains(builder.DefaultProfileName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Default profile '{builder.DefaultProfileName}' conflicts with a fixed StateName."
            );
        }
        return ConfiglueModelDescriptor<TModel>.Current.CreateProfiles(
            (IConfiglueStateRegistry<TModel>)registry,
            catalogSource,
            builder.DefaultProfileName
        );
    }

    public object CreateStateRegistry(
        IServiceProvider? serviceProvider,
        IReadOnlyList<string> reservedNames,
        IConfiglueHostPaths hostPaths
    ) =>
        new ConfiglueFacadeStateRegistry<TModel>(
            name =>
            {
                var resources = new List<object>();
                var resourceSet = new HashSet<object>(ReferenceIdentityComparer.Instance);
                IWritableState<TModel>? runtime = null;
                try
                {
                    var dynamicBuilder = builder.CloneForStateName(name);
                    dynamicBuilder.HostPaths = hostPaths;
                    runtime = ConfiglueModelDescriptor<TModel>.Current.CreateRuntime(
                        dynamicBuilder,
                        serviceProvider,
                        resource =>
                        {
                            ArgumentNullException.ThrowIfNull(resource);
                            if (resource is not IDisposable && resource is not IAsyncDisposable)
                            {
                                throw new ArgumentException(
                                    "An owned resource must implement IDisposable or IAsyncDisposable.",
                                    nameof(resource)
                                );
                            }

                            if (resourceSet.Add(resource))
                            {
                                resources.Add(resource);
                            }
                        }
                    );
                    if (runtime is not IDisposable || runtime is not IAsyncDisposable)
                    {
                        throw new InvalidOperationException(
                            $"The generated runtime for model '{typeof(TModel)}' must support disposal."
                        );
                    }
                    return (runtime, resources.ToArray());
                }
                catch (Exception creationException)
                {
                    List<Exception>? cleanupErrors = null;
                    try
                    {
                        ConfiglueOwnedResources.Dispose(runtime!);
                    }
                    catch (Exception cleanupException)
                    {
                        (cleanupErrors ??= []).Add(cleanupException);
                    }
                    foreach (var resource in resources)
                    {
                        try
                        {
                            ConfiglueOwnedResources.Dispose(resource);
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

    public void Accept(IConfiglueRegistrationVisitor visitor) => visitor.Visit(this);
}
