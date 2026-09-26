using Microsoft.Extensions.DependencyInjection;

namespace Configlue;

/// <summary>Registers Configlue options with a persisted named-profile catalog.</summary>
public static class ConfiglueProfiledOptionsServiceCollectionExtensions
{
    /// <summary>Registers a profile manager backed by profile-specific state sources and a catalog source.</summary>
    public static IServiceCollection AddConfiglueProfiledOptions<TModel, TFragment>(
        this IServiceCollection services,
        Func<IServiceProvider, string, StateSourceSet<TFragment>> profileSourceSetFactory,
        Func<IServiceProvider, StateSource<ConfiglueProfileCatalog>> catalogSourceFactory,
        string defaultProfileName = "default",
        StateWriteRoute writeRoute = default,
        bool validateDataAnnotations = false,
        TimeSpan? onChangeDebounce = null
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(profileSourceSetFactory);
        ArgumentNullException.ThrowIfNull(catalogSourceFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProfileName);
        if (
            defaultProfileName.Contains(':')
            || defaultProfileName.Contains("__", StringComparison.Ordinal)
            || defaultProfileName == nameof(ConfiglueProfileCatalog.ActiveProfileName)
            || defaultProfileName == nameof(ConfiglueProfileCatalog.ProfileNames)
        )
        {
            throw new ArgumentException(
                $"'{defaultProfileName}' is not a valid profile name.",
                nameof(defaultProfileName)
            );
        }

        services.AddConfiglueOptionsRegistry<TModel, TFragment>(
            profileSourceSetFactory,
            writeRoute,
            validateDataAnnotations,
            onChangeDebounce
        );
        services.AddSingleton<IConfiglueProfiledOptions<TModel>>(
            provider => new ConfiglueProfiledOptions<TModel, TFragment>(
                provider.GetRequiredService<IConfiglueOptionsRegistry<TModel>>(),
                catalogSourceFactory(provider),
                defaultProfileName
            )
        );
        return services;
    }
}
