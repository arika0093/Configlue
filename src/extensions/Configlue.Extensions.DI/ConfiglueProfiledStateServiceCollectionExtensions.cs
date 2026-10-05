using Configlue.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue;

/// <summary>Registers Configlue state with a persisted named-profile catalog.</summary>
public static class ConfiglueProfiledStateServiceCollectionExtensions
{
    /// <summary>Registers a profile manager backed by profile-specific state sources and a catalog source.</summary>
    public static IServiceCollection AddConfiglueProfiledState<TModel, TFragment>(
        this IServiceCollection services,
        Func<IServiceProvider, string, StateSourceSet<TFragment>> profileSourceSetFactory,
        Func<IServiceProvider, StateSource<ConfiglueProfileCatalog>> catalogSourceFactory,
        string defaultProfileName = "default",
        StateWritePlan? writePlan = null,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(catalogSourceFactory);
        return services.AddConfiglueProfiledState<TModel, TFragment>(
            profileSourceSetFactory,
            (provider, _) => catalogSourceFactory(provider),
            defaultProfileName,
            writePlan,
            validateDataAnnotations,
            onChangeDebounce,
            writeConflictResolution
        );
    }

    /// <summary>
    /// Registers a profile manager whose profile catalog factory can hand resources to Configlue for
    /// ownership, so synchronous or asynchronous resources are disposed with the manager.
    /// </summary>
    public static IServiceCollection AddConfiglueProfiledState<TModel, TFragment>(
        this IServiceCollection services,
        Func<IServiceProvider, string, StateSourceSet<TFragment>> profileSourceSetFactory,
        Func<
            IServiceProvider,
            Action<object>,
            StateSource<ConfiglueProfileCatalog>
        > catalogSourceFactory,
        string defaultProfileName = "default",
        StateWritePlan? writePlan = null,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(profileSourceSetFactory);
        ArgumentNullException.ThrowIfNull(catalogSourceFactory);
        // Profiles share the logical state-name namespace; only empty or whitespace names are invalid.
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProfileName);

        services.AddConfiglueStateRegistry<TModel, TFragment>(
            profileSourceSetFactory,
            writePlan,
            validateDataAnnotations,
            onChangeDebounce,
            writeConflictResolution: writeConflictResolution
        );
        services.AddSingleton<IConfiglueProfiledState<TModel>>(provider =>
        {
            var ownedResources = new List<object>();
            try
            {
                var catalogSource = catalogSourceFactory(provider, ownedResources.Add);
                return new ConfiglueProfiledState<TModel, TFragment>(
                    provider.GetRequiredService<IConfiglueStateRegistry<TModel>>(),
                    catalogSource,
                    defaultProfileName,
                    ownedResources
                );
            }
            catch
            {
                foreach (var resource in ownedResources)
                {
                    ConfiglueOwnedResources.Dispose(resource);
                }

                throw;
            }
        });
        return services;
    }
}
