using System.Diagnostics;

namespace Configlue.DevTools;

/// <summary>
/// Development-only system-browser launcher for Configlue DevTools.
/// </summary>
/// <remarks>
/// <para>
/// Opens the loopback DevTools launch URL with <c>Process.Start</c> and
/// <c>UseShellExecute</c>. No WebView2/BlazorWebView/embedded controls are used.
/// </para>
/// <para>
/// This is the DevTools-owned default behind the parameterless
/// <c>ConfiglueDevTools.OpenBrowserAsync</c> overloads, so ordinary hosting
/// packages need no host-specific wrappers. Platforms without a desktop shell
/// (for example MAUI on mobile, Unity players, or Godot exports) use their own
/// explicitly-installed <c>Configlue.DevTools.*</c> launcher or a caller-supplied
/// function instead.
/// </para>
/// </remarks>
public sealed class SystemBrowserLauncher : IConfiglueDevToolsBrowserLauncher
{
    /// <inheritdoc />
    public Task OpenAsync(string url, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
