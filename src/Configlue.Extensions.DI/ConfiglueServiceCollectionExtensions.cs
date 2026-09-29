using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue;

/// <summary>Registers generated Configlue options with dependency injection.</summary>
public static class ConfiglueServiceCollectionExtensions
{
    /// <summary>Registers the same model definitions used by non-DI Configlue contexts.</summary>
    public static IServiceCollection AddConfiglue(
        this IServiceCollection services,
        Action<ConfiglueBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ConfiglueBuilder();
        configure(builder);
        return services.AddConfiglueBuilder(builder);
    }

    /// <summary>Registers definitions already collected by a Configlue builder.</summary>
    public static IServiceCollection AddConfiglueBuilder(
        this IServiceCollection services,
        ConfiglueBuilder builder
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(builder);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ConfiglueContext)))
        {
            throw new InvalidOperationException("A Configlue context is already registered.");
        }

        builder.Seal();
        services.AddSingleton<ConfiglueContext>(provider => builder.CreateContext(provider));
        foreach (var registration in builder.Registrations)
        {
            registration.AddServiceDescriptors(services);
        }

        var visitor = new ConfiglueFacadeRegistrationVisitor(services);
        foreach (var registration in builder.Registrations)
        {
            registration.Accept(visitor);
        }

        return services;
    }

    /// <summary>Registers options using a dependency-injection-aware source builder.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        Action<IServiceProvider, StateSourceSetBuilder<TFragment>> configureSources,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        ReadValidationMode readValidationMode = ReadValidationMode.EffectiveThrow,
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureSources);
        return services.AddConfiglueOptions<TModel, TFragment>(
            provider =>
            {
                var sources = new StateSourceSetBuilder<TFragment>();
                configureSources(provider, sources);
                return sources.Build();
            },
            writeRoute,
            validateDataAnnotations,
            onChangeDebounce,
            readValidationMode,
            writeConflictResolution
        );
    }

    /// <summary>Registers options backed by a state-source set created from the service provider.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        Func<IServiceProvider, StateSourceSet<TFragment>> sourceSetFactory,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        ReadValidationMode readValidationMode = ReadValidationMode.EffectiveThrow,
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sourceSetFactory);

        services.AddSingleton(provider => new ConfiglueOptions<TModel, TFragment>(
            sourceSetFactory(provider),
            writeRoute,
            provider.GetServices<IStateSchemaMigration<TFragment>>(),
            provider.GetServices<IConfiglueValidator<TModel>>(),
            validateDataAnnotations,
            onChangeDebounce,
            readValidationMode: readValidationMode,
            writeConflictResolution: writeConflictResolution
        ));
        services.AddSingleton<IReadOnlyOptions<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueOptions<TModel, TFragment>>()
        );
        services.AddSingleton<IWritableOptions<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueOptions<TModel, TFragment>>()
        );
        services.AddSingleton<ISubjectOptions<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueOptions<TModel, TFragment>>()
        );
        services.AddSingleton<IConfiglueInspection<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueOptions<TModel, TFragment>>()
        );
        services.AddSingleton<IConfiglueEditSessions<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueOptions<TModel, TFragment>>()
        );
        services.AddSingleton<IConfiglueDiagnostics<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueOptions<TModel, TFragment>>()
        );
        services.AddSingleton<IConfiglueSources<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueOptions<TModel, TFragment>>()
        );
        return services;
    }

    /// <summary>Registers options backed by an already-created state-source set.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        StateSourceSet<TFragment> sourceSet,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        ReadValidationMode readValidationMode = ReadValidationMode.EffectiveThrow,
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        return services.AddConfiglueOptions<TModel, TFragment>(
            _ => sourceSet,
            writeRoute,
            validateDataAnnotations,
            onChangeDebounce,
            readValidationMode,
            writeConflictResolution
        );
    }

    /// <summary>Registers a named profile using a dependency-injection-aware source builder.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        object serviceKey,
        Action<IServiceProvider, StateSourceSetBuilder<TFragment>> configureSources,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        ReadValidationMode readValidationMode = ReadValidationMode.EffectiveThrow,
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureSources);
        return services.AddConfiglueOptions<TModel, TFragment>(
            serviceKey,
            provider =>
            {
                var sources = new StateSourceSetBuilder<TFragment>();
                configureSources(provider, sources);
                return sources.Build();
            },
            writeRoute,
            validateDataAnnotations,
            onChangeDebounce,
            readValidationMode,
            writeConflictResolution
        );
    }

    /// <summary>Registers a named configuration profile as keyed dependency-injection services.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        object serviceKey,
        Func<IServiceProvider, StateSourceSet<TFragment>> sourceSetFactory,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        ReadValidationMode readValidationMode = ReadValidationMode.EffectiveThrow,
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentNullException.ThrowIfNull(sourceSetFactory);

        services.AddKeyedSingleton<ConfiglueOptions<TModel, TFragment>>(
            serviceKey,
            (provider, key) =>
                new ConfiglueOptions<TModel, TFragment>(
                    sourceSetFactory(provider),
                    writeRoute,
                    provider.GetServices<IStateSchemaMigration<TFragment>>(),
                    provider.GetServices<IConfiglueValidator<TModel>>(),
                    validateDataAnnotations,
                    onChangeDebounce,
                    optionsName: key as string ?? Options.DefaultName,
                    readValidationMode: readValidationMode,
                    writeConflictResolution: writeConflictResolution
                )
        );
        services.AddKeyedSingleton<IReadOnlyOptions<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueOptions<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<IWritableOptions<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueOptions<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<ISubjectOptions<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueOptions<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<IConfiglueInspection<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueOptions<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<IConfiglueEditSessions<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueOptions<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<IConfiglueDiagnostics<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueOptions<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<IConfiglueSources<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueOptions<TModel, TFragment>>(key)
        );
        if (serviceKey is string profileName && profileName != Options.DefaultName)
        {
            services.AddSingleton(new ConfiglueNamedOptionsProfile<TModel>(profileName));
        }

        return services;
    }

    /// <summary>Registers a named profile backed by an already-created state-source set.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        object serviceKey,
        StateSourceSet<TFragment> sourceSet,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        ReadValidationMode readValidationMode = ReadValidationMode.EffectiveThrow,
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        return services.AddConfiglueOptions<TModel, TFragment>(
            serviceKey,
            _ => sourceSet,
            writeRoute,
            validateDataAnnotations,
            onChangeDebounce,
            readValidationMode,
            writeConflictResolution
        );
    }

    /// <summary>Registers a runtime-managed registry that can add and remove named profiles.</summary>
    public static IServiceCollection AddConfiglueOptionsRegistry<TModel, TFragment>(
        this IServiceCollection services,
        Func<IServiceProvider, string, StateSourceSet<TFragment>> sourceSetFactory,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        ReadValidationMode readValidationMode = ReadValidationMode.EffectiveThrow,
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sourceSetFactory);

        services.AddSingleton<IConfiglueOptionsRegistry<TModel>>(
            provider => new ConfiglueOptionsRegistry<TModel, TFragment>(
                profileName => new ConfiglueOptions<TModel, TFragment>(
                    sourceSetFactory(provider, profileName),
                    writeRoute,
                    provider.GetServices<IStateSchemaMigration<TFragment>>(),
                    provider.GetServices<IConfiglueValidator<TModel>>(),
                    validateDataAnnotations,
                    onChangeDebounce,
                    optionsName: profileName,
                    readValidationMode: readValidationMode,
                    writeConflictResolution: writeConflictResolution
                )
            )
        );
        return services;
    }

    private sealed class ConfiglueFacadeRegistrationVisitor(IServiceCollection services)
        : IConfiglueRegistrationVisitor
    {
        public void Visit<TModel>(ConfiglueModelRegistration<TModel> registration)
            where TModel : IConfiglueFacadeModel<TModel>
        {
            if (registration.OptionsName == Options.DefaultName)
            {
                services.AddSingleton<IConfiglueInspection<TModel>>(provider =>
                    (IConfiglueInspection<TModel>)
                        provider.GetRequiredService<IWritableOptions<TModel>>()
                );
                services.AddSingleton<IConfiglueEditSessions<TModel>>(provider =>
                    (IConfiglueEditSessions<TModel>)
                        provider.GetRequiredService<IWritableOptions<TModel>>()
                );
                services.AddSingleton<IConfiglueDiagnostics<TModel>>(provider =>
                    (IConfiglueDiagnostics<TModel>)
                        provider.GetRequiredService<IWritableOptions<TModel>>()
                );
                services.AddSingleton<IConfiglueSources<TModel>>(provider =>
                    (IConfiglueSources<TModel>)
                        provider.GetRequiredService<IWritableOptions<TModel>>()
                );
            }
            else
            {
                if (typeof(TModel).IsClass)
                {
                    services.AddSingleton(
                        new ConfiglueNamedOptionsProfile<TModel>(registration.OptionsName)
                    );
                }

                services.AddKeyedSingleton<IConfiglueInspection<TModel>>(
                    registration.OptionsName,
                    (provider, key) =>
                        (IConfiglueInspection<TModel>)
                            provider.GetRequiredKeyedService<IWritableOptions<TModel>>(key)
                );
                services.AddKeyedSingleton<IConfiglueEditSessions<TModel>>(
                    registration.OptionsName,
                    (provider, key) =>
                        (IConfiglueEditSessions<TModel>)
                            provider.GetRequiredKeyedService<IWritableOptions<TModel>>(key)
                );
                services.AddKeyedSingleton<IConfiglueDiagnostics<TModel>>(
                    registration.OptionsName,
                    (provider, key) =>
                        (IConfiglueDiagnostics<TModel>)
                            provider.GetRequiredKeyedService<IWritableOptions<TModel>>(key)
                );
                services.AddKeyedSingleton<IConfiglueSources<TModel>>(
                    registration.OptionsName,
                    (provider, key) =>
                        (IConfiglueSources<TModel>)
                            provider.GetRequiredKeyedService<IWritableOptions<TModel>>(key)
                );
            }
        }
    }

    /// <summary>Registers a standard Microsoft options validator for Configlue saves.</summary>
    public static IServiceCollection AddConfiglueValidator<TModel>(
        this IServiceCollection services,
        IValidateOptions<TModel> validator
    )
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(validator);
        services.AddSingleton<IConfiglueValidator<TModel>>(
            new ValidateOptionsAdapter<TModel>(validator)
        );
        return services;
    }

    private sealed class ValidateOptionsAdapter<TModel>(IValidateOptions<TModel> validator)
        : IConfiglueValidator<TModel>
        where TModel : class
    {
        public IReadOnlyList<string> Validate(TModel value) => Validate(Options.DefaultName, value);

        public IReadOnlyList<string> Validate(string? name, TModel value)
        {
            var result = validator.Validate(name ?? Options.DefaultName, value);
            return result.Failed ? result.Failures?.ToArray() ?? [] : [];
        }
    }
}
