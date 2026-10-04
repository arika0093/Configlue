using Configlue.DevTools;
using UnityEngine;

namespace Configlue.Hosting.Unity;

/// <summary>
/// Development-only browser-launch hook for Unity players and editor play mode.
/// </summary>
/// <remarks>
/// <para>
/// Thin wrapper only: publish the active loopback session URL via
/// <c>ConfiglueDevTools.Enable</c>, then open the shared browser DevTools UI with
/// <c>Application.OpenURL</c>. No Unity UI Toolkit clone and no remote-player transport.
/// </para>
/// <para>Editor menu integration lives in the editor-only <c>UnityConfiglueDevToolsMenu</c>.</para>
/// </remarks>
public static class UnityConfiglueDevTools
{
    /// <summary>Opens the active session URL with the system browser.</summary>
    public static Task<bool> OpenBrowserAsync(CancellationToken cancellationToken = default) =>
        ConfiglueDevTools.OpenBrowserAsync(new UnityBrowserLauncher(), cancellationToken);

    /// <summary>Opens an explicit launch URL with the system browser.</summary>
    public static Task<bool> OpenBrowserAsync(
        string? launchUrl,
        CancellationToken cancellationToken = default
    ) =>
        ConfiglueDevTools.OpenBrowserAsync(
            launchUrl,
            new UnityBrowserLauncher(),
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

    private sealed class UnityBrowserLauncher : IConfiglueDevToolsBrowserLauncher
    {
        public Task OpenAsync(string url, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Application.OpenURL(url);
            return Task.CompletedTask;
        }
    }
}
