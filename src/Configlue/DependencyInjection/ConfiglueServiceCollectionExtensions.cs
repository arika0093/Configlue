using Microsoft.Extensions.DependencyInjection;

namespace Configlue;

/// <summary>Registers generated Configlue options with dependency injection.</summary>
public static class ConfiglueServiceCollectionExtensions
{
    /// <summary>Registers options backed by a state-source set created from the service provider.</summary>
    public static IServiceCollection AddConfiglueOptions<TModel, TFragment>(
        this IServiceCollection services,
        Func<IServiceProvider, StateSourceSet<TFragment>> sourceSetFactory,
        StateWriteRoute writeRoute = default)
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sourceSetFactory);

        services.AddSingleton(provider => new ConfiglueOptions<TModel, TFragment>(sourceSetFactory(provider), writeRoute));
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
        StateWriteRoute writeRoute = default)
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        return services.AddConfiglueOptions<TModel, TFragment>(_ => sourceSet, writeRoute);
    }
}
