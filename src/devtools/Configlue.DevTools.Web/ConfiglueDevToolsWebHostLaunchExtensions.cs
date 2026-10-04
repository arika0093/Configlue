using Configlue.DevTools;

namespace Configlue.DevTools.Web;

/// <summary>
/// Browser-launch hooks for an already-running loopback DevTools host.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. These helpers never start servers: opening requires an
/// already-running host, so multiple opens reuse the same URL and can never
/// start duplicate servers accidentally.
/// </para>
/// <para>
/// The host runs on its own loopback endpoint (never mapped into a public
/// application host). Public exposure and authorization stay explicit: loopback
/// only plus the per-host session token, with explicit opt-in via
/// <c>ConfiglueDevTools.Enable</c> or an explicit URL.
/// </para>
/// </remarks>
public static class ConfiglueDevToolsWebHostLaunchExtensions
{
    /// <summary>
    /// Publishes a running host launch URL as the active <see cref="ConfiglueDevTools"/> session.
    /// </summary>
    /// <exception cref="InvalidOperationException">The host is not running.</exception>
    public static void PublishAsCurrentSession(this ConfiglueDevToolsWebHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!host.IsRunning || string.IsNullOrWhiteSpace(host.LaunchUrl))
        {
            throw new InvalidOperationException(
                "The DevTools web host is not running. Start it before publishing its launch URL."
            );
        }

        ConfiglueDevTools.Enable(host.LaunchUrl);
    }

    /// <summary>
    /// Opens a running host URL in the system browser. Returns <c>false</c> without
    /// invoking the launcher when the host is not running.
    /// </summary>
    public static Task<bool> OpenBrowserAsync(
        this ConfiglueDevToolsWebHost host,
        IConfiglueDevToolsBrowserLauncher launcher,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(host);
        if (launcher is null)
        {
            throw new ArgumentNullException(nameof(launcher));
        }

        if (!host.IsRunning || string.IsNullOrWhiteSpace(host.LaunchUrl))
        {
            return Task.FromResult(false);
        }

        return ConfiglueDevTools.OpenBrowserAsync(host.LaunchUrl, launcher, cancellationToken);
    }

    /// <summary>
    /// Function-based overload of <see cref="OpenBrowserAsync(ConfiglueDevToolsWebHost, IConfiglueDevToolsBrowserLauncher, CancellationToken)"/>.
    /// </summary>
    public static Task<bool> OpenBrowserAsync(
        this ConfiglueDevToolsWebHost host,
        Func<string, CancellationToken, Task> openAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(host);
        if (openAsync is null)
        {
            throw new ArgumentNullException(nameof(openAsync));
        }

        if (!host.IsRunning || string.IsNullOrWhiteSpace(host.LaunchUrl))
        {
            return Task.FromResult(false);
        }

        return ConfiglueDevTools.OpenBrowserAsync(host.LaunchUrl, openAsync, cancellationToken);
    }
}
