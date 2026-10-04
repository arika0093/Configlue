namespace Configlue.DevTools;

/// <summary>
/// Testable abstraction over opening a DevTools browser URL in the system browser.
/// </summary>
/// <remarks>
/// Development-only. Implementations perform only the host-specific launch
/// (for example <c>Process.Start</c> with <c>UseShellExecute</c>, MAUI
/// <c>Launcher</c>, Unity <c>Application.OpenURL</c>, or Godot
/// <c>OS.ShellOpen</c>). No WebView2/BlazorWebView/embedded controls are used.
/// The launcher never receives secret values, only the loopback launch URL.
/// </remarks>
public interface IConfiglueDevToolsBrowserLauncher
{
    /// <summary>Opens the supplied loopback launch URL in the system browser.</summary>
    /// <param name="url">The DevTools launch URL including the session token.</param>
    /// <param name="cancellationToken">Cancellation for the launch request.</param>
    Task OpenAsync(string url, CancellationToken cancellationToken = default);
}
