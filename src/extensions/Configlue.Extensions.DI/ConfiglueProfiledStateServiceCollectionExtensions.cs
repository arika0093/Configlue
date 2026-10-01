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

        services.AddConfiglueStateRegistry<TModel, TFragment>(
            profileSourceSetFactory,
            writePlan,
            validateDataAnnotations,
            onChangeDebounce,
            readValidationMode: ReadValidationMode.EffectiveThrow,
            writeConflictResolution: writeConflictResolution
        );
        services.AddSingleton<IConfiglueProfiledState<TModel>>(
            provider => new ConfiglueProfiledState<TModel, TFragment>(
                provider.GetRequiredService<IConfiglueStateRegistry<TModel>>(),
                catalogSourceFactory(provider),
                defaultProfileName
            )
        );
        return services;
    }
}
