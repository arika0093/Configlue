using Configlue.DevTools;
using Configlue.DevTools.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue;

/// <summary>
/// Dependency-injection integration for the development-only DevTools web host.
/// </summary>
/// <remarks>
/// Explicit opt-in only. Typical development wiring:
/// <code>
/// #if DEBUG
/// services.AddConfiglueDevToolsWeb(registry);
/// #endif
/// </code>
/// The registry must already reference live runtime instances; nothing is rediscovered.
/// The host starts only when the application resolves it and calls
/// <c>StartAsync</c>, and it is disposed deterministically with the container.
/// </remarks>
public static class ConfiglueDevToolsServiceCollectionExtensions
{
    /// <summary>
    /// Registers an already-populated DevTools registry and its loopback web host.
    /// </summary>
    public static IServiceCollection AddConfiglueDevToolsWeb(
        this IServiceCollection services,
        ConfiglueDevToolsRegistry registry,
        Action<ConfiglueDevToolsWebOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);
        var options = new ConfiglueDevToolsWebOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton(registry);
        services.AddSingleton(provider =>
            ConfiglueDevToolsWebHost.Create(
                provider.GetRequiredService<ConfiglueDevToolsRegistry>(),
                provider.GetRequiredService<ConfiglueDevToolsWebOptions>()
            )
        );
        return services;
    }
}
