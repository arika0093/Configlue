using System.Diagnostics;
using Configlue.DevTools;

namespace Configlue.Hosting.Blazor;

/// <summary>
/// Development-only browser-launch hook for Blazor applications.
/// </summary>
/// <remarks>
/// <para>
/// Blazor applications reuse the same shared browser DevTools UI from
/// <c>Configlue.DevTools.Web</c>; no second Blazor DevTools component is provided here.
/// Run the loopback web host on its own endpoint, publish its <c>LaunchUrl</c> via
/// <c>ConfiglueDevTools.Enable</c>, then open the system browser.
/// </para>
/// </remarks>
public static class BlazorConfiglueDevTools
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
