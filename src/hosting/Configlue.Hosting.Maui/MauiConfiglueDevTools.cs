using System.Diagnostics;
using Configlue.DevTools;
using Microsoft.Maui.ApplicationModel;

namespace Configlue.Hosting.Maui;

/// <summary>
/// Development-only browser-launch hook for MAUI applications (dev menus or startup code).
/// </summary>
/// <remarks>Opens the shared browser DevTools UI in the system browser. No embedded browser control is required.</remarks>
public static class MauiConfiglueDevTools
{
    /// <summary>Opens the active session URL with the platform browser launcher.</summary>
    public static Task<bool> OpenBrowserAsync(CancellationToken cancellationToken = default) =>
        ConfiglueDevTools.OpenBrowserAsync(new MauiBrowserLauncher(), cancellationToken);

    /// <summary>Opens an explicit launch URL with the platform browser launcher.</summary>
    public static Task<bool> OpenBrowserAsync(
        string? launchUrl,
        CancellationToken cancellationToken = default
    ) =>
        ConfiglueDevTools.OpenBrowserAsync(launchUrl, new MauiBrowserLauncher(), cancellationToken);

    /// <summary>Testable overload using a caller-supplied launcher.</summary>
    public static Task<bool> OpenBrowserAsync(
        IConfiglueDevToolsBrowserLauncher launcher,
        CancellationToken cancellationToken = default
    ) => ConfiglueDevTools.OpenBrowserAsync(launcher, cancellationToken);

    /// <summary>Testable overload for an explicit URL using a caller-supplied launcher.</summary>
    public static Task<bool> OpenBrowserAsync(
        string? launchUrl,
        IConfiglueDevToolsBrowserLauncher launcher,
        CancellationToken cancellationToken = default
    ) => ConfiglueDevTools.OpenBrowserAsync(launchUrl, launcher, cancellationToken);

    private sealed class MauiBrowserLauncher : IConfiglueDevToolsBrowserLauncher
    {
        public
#if NET
        async
#endif
        Task OpenAsync(string url, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
#if NET
            await Launcher.Default.OpenAsync(new Uri(url)).ConfigureAwait(false);
#else
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Task.CompletedTask;
#endif
        }
    }
}
