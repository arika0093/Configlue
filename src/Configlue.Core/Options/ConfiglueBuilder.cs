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
    private string _optionsName = Options.DefaultName;
    private StateWriteRoute _writeRoute;
    private bool _validateDataAnnotations;
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
        _sources.Build<TFragment>(serviceProvider);

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

    internal StateSourceSet<TFragment> Build<TFragment>(IServiceProvider? serviceProvider)
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        var sources = new List<StateSource<TFragment>>(_sources.Count);
        foreach (var registration in _sources)
        {
            if (registration.FragmentType != typeof(TFragment))
            {
                throw new InvalidOperationException(
                    $"A source for model fragment '{registration.FragmentType}' cannot be used with '{typeof(TFragment)}'."
                );
            }

            sources.Add((StateSource<TFragment>)registration.Create(serviceProvider));
        }

        return new StateSourceSet<TFragment>(sources);
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
        Type FragmentType { get; }
        object Create(IServiceProvider? serviceProvider);
    }

    private sealed class ConfiglueSourceRegistration<TFragment>(
        Func<IServiceProvider?, StateSource<TFragment>> sourceFactory
    ) : IConfiglueSourceRegistration
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        public Type FragmentType => typeof(TFragment);

        public object Create(IServiceProvider? serviceProvider) =>
            sourceFactory(serviceProvider)
            ?? throw new InvalidOperationException("A source factory returned null.");
    }
}

/// <summary>An isolated, disposable set of registered configuration options.</summary>
public sealed class ConfiglueContext : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<(Type ModelType, string Name), object> _options;
    private readonly object[] _runtimes;
    private int _disposed;

    private ConfiglueContext(
        Dictionary<(Type ModelType, string Name), object> options,
        object[] runtimes
    )
    {
        _options = options;
        _runtimes = runtimes;
    }

    /// <summary>Gets the writable options for a model and optional named instance.</summary>
    public IWritableOptions<TModel> GetOptions<TModel>(string? optionsName = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = (typeof(TModel), optionsName ?? Options.DefaultName);
        return _options.TryGetValue(key, out var options)
            ? (IWritableOptions<TModel>)options
            : throw new KeyNotFoundException(
                $"Model '{typeof(TModel)}' with options name '{key.Item2}' is not registered."
            );
    }

    /// <summary>Disposes the options and watchers owned by this context.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var runtime in _runtimes)
        {
            ((IDisposable)runtime).Dispose();
        }
    }

    /// <summary>Asynchronously disposes the options and watchers owned by this context.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var runtime in _runtimes)
        {
            await ((IAsyncDisposable)runtime).DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static ConfiglueContext Create(
        IReadOnlyList<IConfiglueModelRegistration> registrations,
        IServiceProvider? serviceProvider
    )
    {
        var options = new Dictionary<(Type ModelType, string Name), object>();
        var runtimes = new List<object>(registrations.Count);
        try
        {
            foreach (var registration in registrations)
            {
                var runtime = registration.CreateRuntime(serviceProvider);
                runtimes.Add(runtime);
                if (runtime is not IDisposable || runtime is not IAsyncDisposable)
                {
                    throw new InvalidOperationException(
                        $"The generated runtime for model '{registration.ModelType}' must support disposal."
                    );
                }

                options.Add((registration.ModelType, registration.OptionsName), runtime);
            }

            return new ConfiglueContext(options, runtimes.ToArray());
        }
        catch
        {
            foreach (var runtime in runtimes)
            {
                if (runtime is IDisposable disposable)
                {
                    disposable.Dispose();
                }
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
    object CreateRuntime(IServiceProvider? serviceProvider);
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

    public object CreateRuntime(IServiceProvider? serviceProvider) =>
        TModel.CreateConfiglueRuntime(builder, serviceProvider);

    public void AddServiceDescriptors(IServiceCollection services)
    {
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
