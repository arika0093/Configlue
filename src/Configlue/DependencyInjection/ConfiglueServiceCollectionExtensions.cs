using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue;

/// <summary>Registers generated Configlue options with dependency injection.</summary>
public static class ConfiglueServiceCollectionExtensions
{
    /// <summary>Registers options backed by a state-source set created from the service provider.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        Func<IServiceProvider, StateSourceSet<TFragment>> sourceSetFactory,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false)
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
            validateDataAnnotations));
        services.AddSingleton<IReadOnlyOptions<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueOptions<TModel, TFragment>>());
        services.AddSingleton<IWritableOptions<TModel>>(provider =>
            provider.GetRequiredService<ConfiglueOptions<TModel, TFragment>>());
        return services;
    }

    /// <summary>Registers options backed by an already-created state-source set.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        StateSourceSet<TFragment> sourceSet,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false)
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        return services.AddConfiglueOptions<TModel, TFragment>(_ => sourceSet, writeRoute, validateDataAnnotations);
    }

    /// <summary>Registers a named configuration profile as keyed dependency-injection services.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        object serviceKey,
        Func<IServiceProvider, StateSourceSet<TFragment>> sourceSetFactory,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false)
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentNullException.ThrowIfNull(sourceSetFactory);

        services.AddKeyedSingleton<ConfiglueOptions<TModel, TFragment>>(
            serviceKey,
            (provider, _) => new ConfiglueOptions<TModel, TFragment>(
                sourceSetFactory(provider),
                writeRoute,
                provider.GetServices<IStateSchemaMigration<TFragment>>(),
                provider.GetServices<IConfiglueValidator<TModel>>(),
                validateDataAnnotations));
        services.AddKeyedSingleton<IReadOnlyOptions<TModel>>(
            serviceKey,
            (provider, key) => provider.GetRequiredKeyedService<ConfiglueOptions<TModel, TFragment>>(key));
        services.AddKeyedSingleton<IWritableOptions<TModel>>(
            serviceKey,
            (provider, key) => provider.GetRequiredKeyedService<ConfiglueOptions<TModel, TFragment>>(key));
        return services;
    }

    /// <summary>Registers a named profile backed by an already-created state-source set.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        object serviceKey,
        StateSourceSet<TFragment> sourceSet,
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false)
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        return services.AddConfiglueOptions<TModel, TFragment>(
            serviceKey,
            _ => sourceSet,
            writeRoute,
            validateDataAnnotations);
    }

    /// <summary>Registers a standard Microsoft options validator for Configlue saves.</summary>
    public static IServiceCollection AddConfiglueValidator<TModel>(
        this IServiceCollection services,
        IValidateOptions<TModel> validator)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(validator);
        services.AddSingleton<IConfiglueValidator<TModel>>(new ValidateOptionsAdapter<TModel>(validator));
        return services;
    }

    private sealed class ValidateOptionsAdapter<TModel>(IValidateOptions<TModel> validator) : IConfiglueValidator<TModel>
        where TModel : class
    {
        public IReadOnlyList<string> Validate(TModel value)
        {
            var result = validator.Validate(Options.DefaultName, value);
            return result.Failed ? result.Failures?.ToArray() ?? [] : [];
        }
    }
}
