using System.Diagnostics;
using Configlue.DevTools;
using Microsoft.Maui.ApplicationModel;

namespace Configlue.DevTools.Maui;

/// <summary>
/// Development-only browser-launch hook for MAUI applications (dev menus or startup code).
/// </summary>
/// <remarks>
/// <para>
/// Explicitly-installed tooling only: the normal <c>Configlue.Hosting.Maui</c>
/// package never references this package. Opens the shared browser DevTools UI
/// with the MAUI platform <c>Launcher</c> API, which is required on mobile
/// targets where a desktop shell process is unavailable. No embedded browser
/// control is required.
/// </para>
/// <para>
/// Publish the active loopback session URL via <c>ConfiglueDevTools.Enable</c>
/// before opening.
/// </para>
/// </remarks>
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
