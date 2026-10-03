using Configlue.CompilerServices;
using Configlue.Extensibility;
using Configlue.Resources;
using Microsoft.Extensions.DependencyInjection;

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
        var visitor = new ConfiglueFacadeRegistrationVisitor(services, builder.HostPaths);
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
        StateWritePlan? writePlan = null,
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
            writePlan,
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
        StateWritePlan? writePlan = null,
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
            writePlan,
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
        StateWritePlan? writePlan = null,
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
            writePlan,
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
        StateWritePlan? writePlan = null,
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
            writePlan,
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
        StateWritePlan? writePlan = null,
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
                    writePlan,
                    provider.GetServices<IStateSchemaMigration<TFragment>>(),
                    provider.GetServices<IConfiglueValidator<TModel>>(),
                    validateDataAnnotations,
                    onChangeDebounce,
                    stateName: key as string ?? ConfiglueDefaultNames.DefaultState,
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
        if (serviceKey is string profileName && profileName != ConfiglueDefaultNames.DefaultState)
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
        StateWritePlan? writePlan = null,
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
            writePlan,
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
        StateWritePlan? writePlan = null,
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
                    writePlan,
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

    private sealed class ConfiglueFacadeRegistrationVisitor(
        IServiceCollection services,
        IConfiglueHostPaths hostPaths
    ) : IConfiglueRegistrationVisitor
    {
        public void Visit<TModel>(ConfiglueModelRegistration<TModel> registration)
            where TModel : IConfiglueFacadeModel<TModel>
        {
            AddModelServiceDescriptors(registration);
            if (registration.RuntimeLifetime == RuntimeLifetimeRequirement.Scoped)
            {
                return;
            }
            if (registration.StateName == ConfiglueDefaultNames.DefaultState)
            {
                if (registration.IsPerSubject)
                {
                    // Per-subject inspection/edit sessions must resolve through the scoped
                    // CurrentSubjectState. Keep only subject-agnostic facades as shared singletons
                    // so the raw runtime descriptor cannot overwrite the scoped fix.
                    services.AddSingleton<IConfiglueDiagnostics<TModel>>(provider =>
                        (IConfiglueDiagnostics<TModel>)
                            provider
                                .GetRequiredService<ConfiglueContext>()
                                .GetState<TModel>(registration.StateName)
                    );
                    services.AddSingleton<IConfiglueSources<TModel>>(provider =>
                        (IConfiglueSources<TModel>)
                            provider
                                .GetRequiredService<ConfiglueContext>()
                                .GetState<TModel>(registration.StateName)
                    );
                }
                else
                {
                    Func<IServiceProvider, IWritableState<TModel>> getRuntime = provider =>
                        provider.GetRequiredService<IWritableState<TModel>>();
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
            }
            else
            {
                if (typeof(TModel).IsClass)
                {
                    services.AddSingleton(
                        new ConfiglueNamedStateProfile<TModel>(registration.StateName)
                    );
                }

                if (registration.IsPerSubject)
                {
                    services.AddKeyedSingleton<IConfiglueDiagnostics<TModel>>(
                        registration.StateName,
                        (provider, _) =>
                            (IConfiglueDiagnostics<TModel>)
                                provider
                                    .GetRequiredService<ConfiglueContext>()
                                    .GetState<TModel>(registration.StateName)
                    );
                    services.AddKeyedSingleton<IConfiglueSources<TModel>>(
                        registration.StateName,
                        (provider, _) =>
                            (IConfiglueSources<TModel>)
                                provider
                                    .GetRequiredService<ConfiglueContext>()
                                    .GetState<TModel>(registration.StateName)
                    );
                }
                else
                {
                    Func<IServiceProvider, IWritableState<TModel>> getRuntime = provider =>
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

        private void AddModelServiceDescriptors<TModel>(
            ConfiglueModelRegistration<TModel> registration
        )
            where TModel : IConfiglueFacadeModel<TModel>
        {
            if (registration.EnableDynamicStates)
            {
                services.AddSingleton<IConfiglueStateRegistry<TModel>>(provider =>
                    provider.GetRequiredService<ConfiglueContext>().GetStateRegistry<TModel>()
                );
            }
            if (registration.EnableProfiles)
            {
                services.AddSingleton<IConfiglueProfiledState<TModel>>(provider =>
                    provider.GetRequiredService<ConfiglueContext>().GetProfiledState<TModel>()
                );
            }
            if (registration.RuntimeLifetime == RuntimeLifetimeRequirement.Scoped)
            {
                AddScopedStateServices(registration);
                return;
            }
            if (registration.IsPerSubject)
            {
                var subjectAccessorType = registration.SubjectAccessorType!;
                if (registration.StateName.Length == 0)
                {
                    services.AddScoped(provider => new CurrentSubjectState<TModel>(
                        provider
                            .GetRequiredService<ConfiglueContext>()
                            .GetSubjectState<TModel>(registration.StateName),
                        (IConfiglueSubjectAccessor)provider.GetRequiredService(subjectAccessorType)
                    ));
                    services.AddScoped<IReadOnlyState<TModel>>(provider =>
                        provider.GetRequiredService<CurrentSubjectState<TModel>>()
                    );
                    services.AddScoped<IWritableState<TModel>>(provider =>
                        provider.GetRequiredService<CurrentSubjectState<TModel>>()
                    );
                    services.AddScoped<IConfiglueInspection<TModel>>(provider =>
                        provider.GetRequiredService<CurrentSubjectState<TModel>>()
                    );
                    services.AddScoped<IConfiglueEditSessions<TModel>>(provider =>
                        provider.GetRequiredService<CurrentSubjectState<TModel>>()
                    );
                    services.AddSingleton<ISubjectState<TModel>>(provider =>
                        provider
                            .GetRequiredService<ConfiglueContext>()
                            .GetSubjectState<TModel>(registration.StateName)
                    );
                }
                else
                {
                    services.AddKeyedScoped<CurrentSubjectState<TModel>>(
                        registration.StateName,
                        (provider, _) =>
                            new CurrentSubjectState<TModel>(
                                provider
                                    .GetRequiredService<ConfiglueContext>()
                                    .GetSubjectState<TModel>(registration.StateName),
                                (IConfiglueSubjectAccessor)
                                    provider.GetRequiredService(subjectAccessorType)
                            )
                    );
                    services.AddKeyedScoped<IReadOnlyState<TModel>>(
                        registration.StateName,
                        (provider, key) =>
                            provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                    );
                    services.AddKeyedScoped<IWritableState<TModel>>(
                        registration.StateName,
                        (provider, key) =>
                            provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                    );
                    services.AddKeyedScoped<IConfiglueInspection<TModel>>(
                        registration.StateName,
                        (provider, key) =>
                            provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                    );
                    services.AddKeyedScoped<IConfiglueEditSessions<TModel>>(
                        registration.StateName,
                        (provider, key) =>
                            provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                    );
                    services.AddKeyedSingleton<ISubjectState<TModel>>(
                        registration.StateName,
                        (provider, _) =>
                            provider
                                .GetRequiredService<ConfiglueContext>()
                                .GetSubjectState<TModel>(registration.StateName)
                    );
                }
            }
            else if (registration.StateName.Length == 0)
            {
                services.AddSingleton<IReadOnlyState<TModel>>(provider =>
                    provider
                        .GetRequiredService<ConfiglueContext>()
                        .GetState<TModel>(registration.StateName)
                );
                services.AddSingleton<IWritableState<TModel>>(provider =>
                    provider
                        .GetRequiredService<ConfiglueContext>()
                        .GetState<TModel>(registration.StateName)
                );
                services.AddSingleton<ISubjectState<TModel>>(provider =>
                    provider
                        .GetRequiredService<ConfiglueContext>()
                        .GetSubjectState<TModel>(registration.StateName)
                );
            }
            else
            {
                services.AddKeyedSingleton<IReadOnlyState<TModel>>(
                    registration.StateName,
                    (provider, _) =>
                        provider
                            .GetRequiredService<ConfiglueContext>()
                            .GetState<TModel>(registration.StateName)
                );
                services.AddKeyedSingleton<IWritableState<TModel>>(
                    registration.StateName,
                    (provider, _) =>
                        provider
                            .GetRequiredService<ConfiglueContext>()
                            .GetState<TModel>(registration.StateName)
                );
                services.AddKeyedSingleton<ISubjectState<TModel>>(
                    registration.StateName,
                    (provider, _) =>
                        provider
                            .GetRequiredService<ConfiglueContext>()
                            .GetSubjectState<TModel>(registration.StateName)
                );
            }
        }

        private void AddScopedStateServices<TModel>(ConfiglueModelRegistration<TModel> registration)
            where TModel : IConfiglueFacadeModel<TModel>
        {
            ConfiglueScopedRuntime<TModel> CreateHolder(IServiceProvider provider)
            {
                var resources = new List<object>();
                var resourceSet = new HashSet<object>(ReferenceIdentityComparer.Instance);
                var runtime =
                    (IConfiglueRuntimeState<TModel>)
                        registration.CreateRuntime(
                            provider,
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
                            },
                            hostPaths
                        );
                return new ConfiglueScopedRuntime<TModel>(runtime, resources);
            }

            Func<IServiceProvider, IWritableState<TModel>> getHolderRuntime =
                registration.StateName.Length == 0
                    ? provider =>
                        provider.GetRequiredService<ConfiglueScopedRuntime<TModel>>().Runtime
                    : provider =>
                        provider
                            .GetRequiredKeyedService<ConfiglueScopedRuntime<TModel>>(
                                registration.StateName
                            )
                            .Runtime;

            if (registration.StateName.Length == 0)
            {
                services.AddScoped(CreateHolder);
            }
            else
            {
                services.AddKeyedScoped<ConfiglueScopedRuntime<TModel>>(
                    registration.StateName,
                    (provider, _) => CreateHolder(provider)
                );
            }

            if (registration.IsPerSubject)
            {
                var subjectAccessorType = registration.SubjectAccessorType!;
                if (registration.StateName.Length == 0)
                {
                    services.AddScoped(provider => new CurrentSubjectState<TModel>(
                        (ISubjectState<TModel>)getHolderRuntime(provider),
                        (IConfiglueSubjectAccessor)provider.GetRequiredService(subjectAccessorType)
                    ));
                    services.AddScoped<IReadOnlyState<TModel>>(provider =>
                        provider.GetRequiredService<CurrentSubjectState<TModel>>()
                    );
                    services.AddScoped<IWritableState<TModel>>(provider =>
                        provider.GetRequiredService<CurrentSubjectState<TModel>>()
                    );
                    services.AddScoped<IConfiglueInspection<TModel>>(provider =>
                        provider.GetRequiredService<CurrentSubjectState<TModel>>()
                    );
                    services.AddScoped<IConfiglueEditSessions<TModel>>(provider =>
                        provider.GetRequiredService<CurrentSubjectState<TModel>>()
                    );
                    AddScopedSubjectAgnosticFacades(getHolderRuntime);
                }
                else
                {
                    services.AddKeyedScoped<CurrentSubjectState<TModel>>(
                        registration.StateName,
                        (provider, _) =>
                            new CurrentSubjectState<TModel>(
                                (ISubjectState<TModel>)getHolderRuntime(provider),
                                (IConfiglueSubjectAccessor)
                                    provider.GetRequiredService(subjectAccessorType)
                            )
                    );
                    services.AddKeyedScoped<IReadOnlyState<TModel>>(
                        registration.StateName,
                        (provider, key) =>
                            provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                    );
                    services.AddKeyedScoped<IWritableState<TModel>>(
                        registration.StateName,
                        (provider, key) =>
                            provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                    );
                    services.AddKeyedScoped<IConfiglueInspection<TModel>>(
                        registration.StateName,
                        (provider, key) =>
                            provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                    );
                    services.AddKeyedScoped<IConfiglueEditSessions<TModel>>(
                        registration.StateName,
                        (provider, key) =>
                            provider.GetRequiredKeyedService<CurrentSubjectState<TModel>>(key)
                    );
                    AddKeyedScopedSubjectAgnosticFacades(
                        registration.StateName,
                        (provider, _) => getHolderRuntime(provider)
                    );
                }
            }
            else if (registration.StateName.Length == 0)
            {
                services.AddScoped<IReadOnlyState<TModel>>(provider => getHolderRuntime(provider));
                services.AddScoped<IWritableState<TModel>>(provider => getHolderRuntime(provider));
                AddScopedRuntimeFacades(getHolderRuntime);
            }
            else
            {
                services.AddKeyedScoped<IReadOnlyState<TModel>>(
                    registration.StateName,
                    (provider, _) => getHolderRuntime(provider)
                );
                services.AddKeyedScoped<IWritableState<TModel>>(
                    registration.StateName,
                    (provider, _) => getHolderRuntime(provider)
                );
                AddKeyedScopedRuntimeFacades(
                    registration.StateName,
                    (provider, _) => getHolderRuntime(provider)
                );
            }
        }

        private void AddScopedRuntimeFacades<TModel>(
            Func<IServiceProvider, IWritableState<TModel>> getRuntime
        )
            where TModel : IConfiglueFacadeModel<TModel>
        {
            services.AddScoped<ISubjectState<TModel>>(provider =>
                (ISubjectState<TModel>)getRuntime(provider)
            );
            services.AddScoped<IConfiglueInspection<TModel>>(provider =>
                (IConfiglueInspection<TModel>)getRuntime(provider)
            );
            services.AddScoped<IConfiglueEditSessions<TModel>>(provider =>
                (IConfiglueEditSessions<TModel>)getRuntime(provider)
            );
            services.AddScoped<IConfiglueDiagnostics<TModel>>(provider =>
                (IConfiglueDiagnostics<TModel>)getRuntime(provider)
            );
            services.AddScoped<IConfiglueSources<TModel>>(provider =>
                (IConfiglueSources<TModel>)getRuntime(provider)
            );
        }

        private void AddKeyedScopedRuntimeFacades<TModel>(
            object serviceKey,
            Func<IServiceProvider, object?, IWritableState<TModel>> getRuntime
        )
            where TModel : IConfiglueFacadeModel<TModel>
        {
            services.AddKeyedScoped<ISubjectState<TModel>>(
                serviceKey,
                (provider, key) => (ISubjectState<TModel>)getRuntime(provider, key)
            );
            services.AddKeyedScoped<IConfiglueInspection<TModel>>(
                serviceKey,
                (provider, key) => (IConfiglueInspection<TModel>)getRuntime(provider, key)
            );
            services.AddKeyedScoped<IConfiglueEditSessions<TModel>>(
                serviceKey,
                (provider, key) => (IConfiglueEditSessions<TModel>)getRuntime(provider, key)
            );
            services.AddKeyedScoped<IConfiglueDiagnostics<TModel>>(
                serviceKey,
                (provider, key) => (IConfiglueDiagnostics<TModel>)getRuntime(provider, key)
            );
            services.AddKeyedScoped<IConfiglueSources<TModel>>(
                serviceKey,
                (provider, key) => (IConfiglueSources<TModel>)getRuntime(provider, key)
            );
        }

        private void AddScopedSubjectAgnosticFacades<TModel>(
            Func<IServiceProvider, IWritableState<TModel>> getRuntime
        )
            where TModel : IConfiglueFacadeModel<TModel>
        {
            services.AddScoped<ISubjectState<TModel>>(provider =>
                (ISubjectState<TModel>)getRuntime(provider)
            );
            services.AddScoped<IConfiglueDiagnostics<TModel>>(provider =>
                (IConfiglueDiagnostics<TModel>)getRuntime(provider)
            );
            services.AddScoped<IConfiglueSources<TModel>>(provider =>
                (IConfiglueSources<TModel>)getRuntime(provider)
            );
        }

        private void AddKeyedScopedSubjectAgnosticFacades<TModel>(
            object serviceKey,
            Func<IServiceProvider, object?, IWritableState<TModel>> getRuntime
        )
            where TModel : IConfiglueFacadeModel<TModel>
        {
            services.AddKeyedScoped<ISubjectState<TModel>>(
                serviceKey,
                (provider, key) => (ISubjectState<TModel>)getRuntime(provider, key)
            );
            services.AddKeyedScoped<IConfiglueDiagnostics<TModel>>(
                serviceKey,
                (provider, key) => (IConfiglueDiagnostics<TModel>)getRuntime(provider, key)
            );
            services.AddKeyedScoped<IConfiglueSources<TModel>>(
                serviceKey,
                (provider, key) => (IConfiglueSources<TModel>)getRuntime(provider, key)
            );
        }
    }
}
