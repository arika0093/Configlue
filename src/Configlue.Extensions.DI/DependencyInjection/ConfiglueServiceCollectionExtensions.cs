using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue;

/// <summary>Registers generated Configlue options with dependency injection.</summary>
public static class ConfiglueServiceCollectionExtensions
{
    /// <summary>Registers options using a dependency-injection-aware source builder.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        Action<IServiceProvider, StateSourceSetBuilder<TFragment>> configureSources,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false,
        TimeSpan? onChangeDebounce = null
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
            onChangeDebounce
        );
    }

    /// <summary>Registers options backed by a state-source set created from the service provider.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        Func<IServiceProvider, StateSourceSet<TFragment>> sourceSetFactory,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false,
        TimeSpan? onChangeDebounce = null
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
            onChangeDebounce
        ));
        services.AddSingleton<IReadOnlyOptions<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueOptions<TModel, TFragment>>()
        );
        services.AddSingleton<IWritableOptions<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueOptions<TModel, TFragment>>()
        );
        services.AddConfiglueMicrosoftOptions<TModel>();
        return services;
    }

    /// <summary>Registers options backed by an already-created state-source set.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        StateSourceSet<TFragment> sourceSet,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false,
        TimeSpan? onChangeDebounce = null
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        return services.AddConfiglueOptions<TModel, TFragment>(
            _ => sourceSet,
            writeRoute,
            validateDataAnnotations,
            onChangeDebounce
        );
    }

    /// <summary>Registers a named profile using a dependency-injection-aware source builder.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        object serviceKey,
        Action<IServiceProvider, StateSourceSetBuilder<TFragment>> configureSources,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false,
        TimeSpan? onChangeDebounce = null
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
            onChangeDebounce
        );
    }

    /// <summary>Registers a named configuration profile as keyed dependency-injection services.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        object serviceKey,
        Func<IServiceProvider, StateSourceSet<TFragment>> sourceSetFactory,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false,
        TimeSpan? onChangeDebounce = null
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
                    optionsName: key as string ?? Options.DefaultName
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
        if (serviceKey is string profileName && profileName != Options.DefaultName)
        {
            services.AddSingleton(new ConfiglueNamedOptionsProfile<TModel>(profileName));
        }

        services.AddConfiglueMicrosoftOptions<TModel>();
        return services;
    }

    /// <summary>Registers a named profile backed by an already-created state-source set.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        object serviceKey,
        StateSourceSet<TFragment> sourceSet,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false,
        TimeSpan? onChangeDebounce = null
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
            onChangeDebounce
        );
    }

    /// <summary>Registers a runtime-managed registry that can add and remove named profiles.</summary>
    public static IServiceCollection AddConfiglueOptionsRegistry<TModel, TFragment>(
        this IServiceCollection services,
        Func<IServiceProvider, string, StateSourceSet<TFragment>> sourceSetFactory,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false,
        TimeSpan? onChangeDebounce = null
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
                    optionsName: profileName
                )
            )
        );
        services.AddConfiglueMicrosoftOptions<TModel>();
        return services;
    }

    private static void AddConfiglueMicrosoftOptions<TModel>(this IServiceCollection services)
    {
        if (!typeof(TModel).IsValueType)
        {
            var modelType = typeof(TModel);
            var resolverType = typeof(ConfiglueMicrosoftOptionsResolver<>).MakeGenericType(
                modelType
            );
            var optionsType = typeof(IOptions<>).MakeGenericType(modelType);
            var snapshotType = typeof(IOptionsSnapshot<>).MakeGenericType(modelType);
            var monitorType = typeof(IOptionsMonitor<>).MakeGenericType(modelType);
            var valueAdapterType = typeof(ConfiglueMicrosoftOptionsValue<>).MakeGenericType(
                modelType
            );
            var snapshotAdapterType = typeof(ConfiglueMicrosoftOptionsSnapshot<>).MakeGenericType(
                modelType
            );
            var monitorAdapterType = typeof(ConfiglueMicrosoftOptionsMonitor<>).MakeGenericType(
                modelType
            );

            services.AddSingleton(resolverType, provider => CreateAdapter(resolverType, provider));
            services.AddSingleton(
                optionsType,
                provider =>
                    CreateAdapter(valueAdapterType, GetRequiredService(provider, resolverType))
            );
            services.AddScoped(
                snapshotType,
                provider =>
                    CreateAdapter(snapshotAdapterType, GetRequiredService(provider, resolverType))
            );
            services.AddSingleton(
                monitorType,
                provider =>
                    CreateAdapter(
                        monitorAdapterType,
                        GetRequiredService(provider, resolverType),
                        GetNamedOptionsProfiles<TModel>(provider)
                    )
            );
        }
    }

    private static object GetRequiredService(IServiceProvider provider, Type serviceType) =>
        provider.GetService(serviceType)
        ?? throw new InvalidOperationException($"Service '{serviceType}' is not registered.");

    private static object GetNamedOptionsProfiles<TModel>(IServiceProvider provider) =>
        provider.GetServices<ConfiglueNamedOptionsProfile<TModel>>();

    private static object CreateAdapter(Type adapterType, params object[] arguments) =>
        Activator.CreateInstance(
            adapterType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
            binder: null,
            args: arguments,
            culture: null
        )
        ?? throw new InvalidOperationException(
            $"Could not create Configlue options adapter '{adapterType}'."
        );

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
