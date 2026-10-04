using System.Diagnostics;
using Configlue.DevTools;

namespace Configlue.Hosting.WinUI;

/// <summary>
/// Development-only browser-launch hook for WinUI applications (dev menus or startup code).
/// </summary>
/// <remarks>Opens the shared browser DevTools UI in the system browser. No embedded browser control is required.</remarks>
public static class WinUIConfiglueDevTools
{
    /// <summary>Opens the active session URL with the system browser.</summary>
    public static Task<bool> OpenBrowserAsync(CancellationToken cancellationToken = default) =>
        ConfiglueDevTools.OpenBrowserAsync(new SystemBrowserLauncher(), cancellationToken);

    /// <summary>Opens an explicit launch URL with the system browser.</summary>
    public static Task<bool> OpenBrowserAsync(
        string? launchUrl,
        CancellationToken cancellationToken = default
    ) =>
        ConfiglueDevTools.OpenBrowserAsync(
            launchUrl,
            new SystemBrowserLauncher(),
            cancellationToken
        );

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

    private sealed class SystemBrowserLauncher : IConfiglueDevToolsBrowserLauncher
    {
        public Task OpenAsync(string url, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Task.CompletedTask;
        }
    }
}
