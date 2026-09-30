using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Extensions.MSOptions;

/// <summary>Registers opt-in adapters for Microsoft's options abstractions.</summary>
public static class ConfiglueMicrosoftOptionsServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IOptions{TOptions}"/>, snapshots, and monitors for a Configlue model.</summary>
    public static IServiceCollection AddConfiglueMicrosoftOptions<TModel>(
        this IServiceCollection services
    )
    {
        ArgumentNullException.ThrowIfNull(services);
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
                        provider.GetServices<ConfiglueNamedStateProfile<TModel>>()
                    )
            );
        }

        return services;
    }

    private static object GetRequiredService(IServiceProvider provider, Type serviceType) =>
        provider.GetService(serviceType)
        ?? throw new InvalidOperationException($"Service '{serviceType}' is not registered.");

    private static object CreateAdapter(Type adapterType, params object[] arguments) =>
        Activator.CreateInstance(
            adapterType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
            binder: null,
            args: arguments,
            culture: null
        )
        ?? throw new InvalidOperationException(
            $"Could not create Configlue Microsoft Options adapter '{adapterType}'."
        );
}
