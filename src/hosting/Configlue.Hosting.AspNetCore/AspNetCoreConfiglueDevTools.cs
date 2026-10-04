using System.Diagnostics;
using Configlue.DevTools;

namespace Configlue.Hosting.AspNetCore;

/// <summary>
/// Development-only browser-launch hook for the shared DevTools browser UI.
/// </summary>
/// <remarks>
/// <para>
/// Thin wrapper only: ensure a loopback <c>ConfiglueDevToolsWebHost</c> is running
/// (its own loopback endpoint; never mapped into a public host), publish its
/// <c>LaunchUrl</c> via <c>ConfiglueDevTools.Enable</c>, then open the system browser.
/// </para>
/// <para>Every host opens the same shared browser UI. No embedded WebView is used.</para>
/// </remarks>
public static class AspNetCoreConfiglueDevTools
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
