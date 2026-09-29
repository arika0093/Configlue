using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue;

/// <summary>Registers generated Configlue state with dependency injection.</summary>
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

    /// <summary>Registers state using a dependency-injection-aware source builder.</summary>
    public static IServiceCollection AddConfiglueState<TModel, TFragment>(
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
        return services.AddConfiglueState<TModel, TFragment>(
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

    /// <summary>Registers state backed by a state-source set created from the service provider.</summary>
    public static IServiceCollection AddConfiglueState<TModel, TFragment>(
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

        services.AddSingleton(provider => new ConfiglueRuntime<TModel, TFragment>(
            sourceSetFactory(provider),
            writeRoute,
            provider.GetServices<IStateSchemaMigration<TFragment>>(),
            provider.GetServices<IConfiglueValidator<TModel>>(),
            validateDataAnnotations,
            onChangeDebounce,
            readValidationMode: readValidationMode,
            writeConflictResolution: writeConflictResolution
        ));
        services.AddSingleton<IReadOnlyState<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueRuntime<TModel, TFragment>>()
        );
        services.AddSingleton<IWritableState<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueRuntime<TModel, TFragment>>()
        );
        services.AddSingleton<ISubjectState<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueRuntime<TModel, TFragment>>()
        );
        services.AddSingleton<IConfiglueInspection<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueRuntime<TModel, TFragment>>()
        );
        services.AddSingleton<IConfiglueEditSessions<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueRuntime<TModel, TFragment>>()
        );
        services.AddSingleton<IConfiglueDiagnostics<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueRuntime<TModel, TFragment>>()
        );
        services.AddSingleton<IConfiglueSources<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueRuntime<TModel, TFragment>>()
        );
        return services;
    }

    /// <summary>Registers state backed by an already-created state-source set.</summary>
    public static IServiceCollection AddConfiglueState<TModel, TFragment>(
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
        return services.AddConfiglueState<TModel, TFragment>(
            _ => sourceSet,
            writeRoute,
            validateDataAnnotations,
            onChangeDebounce,
            readValidationMode,
            writeConflictResolution
        );
    }

    /// <summary>Registers a named profile using a dependency-injection-aware source builder.</summary>
    public static IServiceCollection AddConfiglueState<TModel, TFragment>(
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
        return services.AddConfiglueState<TModel, TFragment>(
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
    public static IServiceCollection AddConfiglueState<TModel, TFragment>(
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

        services.AddKeyedSingleton<ConfiglueRuntime<TModel, TFragment>>(
            serviceKey,
            (provider, key) =>
                new ConfiglueRuntime<TModel, TFragment>(
                    sourceSetFactory(provider),
                    writeRoute,
                    provider.GetServices<IStateSchemaMigration<TFragment>>(),
                    provider.GetServices<IConfiglueValidator<TModel>>(),
                    validateDataAnnotations,
                    onChangeDebounce,
                    stateName: key as string ?? Options.DefaultName,
                    readValidationMode: readValidationMode,
                    writeConflictResolution: writeConflictResolution
                )
        );
        services.AddKeyedSingleton<IReadOnlyState<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueRuntime<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<IWritableState<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueRuntime<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<ISubjectState<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueRuntime<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<IConfiglueInspection<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueRuntime<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<IConfiglueEditSessions<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueRuntime<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<IConfiglueDiagnostics<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueRuntime<TModel, TFragment>>(key)
        );
        services.AddKeyedSingleton<IConfiglueSources<TModel>>(
            serviceKey,
            (provider, key) =>
                provider.GetRequiredKeyedService<ConfiglueRuntime<TModel, TFragment>>(key)
        );
        if (serviceKey is string profileName && profileName != Options.DefaultName)
        {
            services.AddSingleton(new ConfiglueNamedStateProfile<TModel>(profileName));
        }

        return services;
    }

    /// <summary>Registers a named profile backed by an already-created state-source set.</summary>
    public static IServiceCollection AddConfiglueState<TModel, TFragment>(
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
        return services.AddConfiglueState<TModel, TFragment>(
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
    public static IServiceCollection AddConfiglueStateRegistry<TModel, TFragment>(
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

        services.AddSingleton<IConfiglueStateRegistry<TModel>>(
            provider => new ConfiglueStateRegistry<TModel, TFragment>(
                profileName => new ConfiglueRuntime<TModel, TFragment>(
                    sourceSetFactory(provider, profileName),
                    writeRoute,
                    provider.GetServices<IStateSchemaMigration<TFragment>>(),
                    provider.GetServices<IConfiglueValidator<TModel>>(),
                    validateDataAnnotations,
                    onChangeDebounce,
                    stateName: profileName,
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
            if (registration.StateName == Options.DefaultName)
            {
                Func<IServiceProvider, IWritableState<TModel>> getRuntime =
                    registration.IsPerSubject
                        ? provider =>
                            provider
                                .GetRequiredService<ConfiglueContext>()
                                .GetState<TModel>(registration.StateName)
                        : provider => provider.GetRequiredService<IWritableState<TModel>>();
                services.AddSingleton<IConfiglueInspection<TModel>>(provider =>
                    (IConfiglueInspection<TModel>)getRuntime(provider)
                );
                services.AddSingleton<IConfiglueEditSessions<TModel>>(provider =>
                    (IConfiglueEditSessions<TModel>)getRuntime(provider)
                );
                services.AddSingleton<IConfiglueDiagnostics<TModel>>(provider =>
                    (IConfiglueDiagnostics<TModel>)getRuntime(provider)
                );
                services.AddSingleton<IConfiglueSources<TModel>>(provider =>
                    (IConfiglueSources<TModel>)getRuntime(provider)
                );
            }
            else
            {
                if (typeof(TModel).IsClass)
                {
                    services.AddSingleton(
                        new ConfiglueNamedStateProfile<TModel>(registration.StateName)
                    );
                }

                Func<IServiceProvider, IWritableState<TModel>> getRuntime =
                    registration.IsPerSubject
                        ? provider =>
                            provider
                                .GetRequiredService<ConfiglueContext>()
                                .GetState<TModel>(registration.StateName)
                        : provider =>
                            provider.GetRequiredKeyedService<IWritableState<TModel>>(
                                registration.StateName
                            );
                services.AddKeyedSingleton<IConfiglueInspection<TModel>>(
                    registration.StateName,
                    (provider, _) => (IConfiglueInspection<TModel>)getRuntime(provider)
                );
                services.AddKeyedSingleton<IConfiglueEditSessions<TModel>>(
                    registration.StateName,
                    (provider, _) => (IConfiglueEditSessions<TModel>)getRuntime(provider)
                );
                services.AddKeyedSingleton<IConfiglueDiagnostics<TModel>>(
                    registration.StateName,
                    (provider, _) => (IConfiglueDiagnostics<TModel>)getRuntime(provider)
                );
                services.AddKeyedSingleton<IConfiglueSources<TModel>>(
                    registration.StateName,
                    (provider, _) => (IConfiglueSources<TModel>)getRuntime(provider)
                );
            }
        }
    }

    /// <summary>Registers a standard Microsoft options validator for state writes.</summary>
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
