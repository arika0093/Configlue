using Configlue.DevTools;

namespace Configlue.DevTools.Godot;

/// <summary>
/// Development-only browser-launch hook for Godot projects.
/// </summary>
/// <remarks>
/// <para>
/// Explicitly-installed tooling only: the normal <c>Configlue.Hosting.Godot</c>
/// package never references this package. Publish the active loopback session
/// URL via <c>ConfiglueDevTools.Enable</c>, then open the shared browser
/// DevTools UI with <c>OS.ShellOpen</c>. No Godot-control clone of the web UI.
/// </para>
/// <para>Editor menu integration lives in the editor-only <c>ConfiglueDevToolsEditorPlugin</c>.</para>
/// </remarks>
public static class GodotConfiglueDevTools
{
    /// <summary>Opens the active session URL with the system browser.</summary>
    public static Task<bool> OpenBrowserAsync(CancellationToken cancellationToken = default) =>
        ConfiglueDevTools.OpenBrowserAsync(new GodotBrowserLauncher(), cancellationToken);

    /// <summary>Opens an explicit launch URL with the system browser.</summary>
    public static Task<bool> OpenBrowserAsync(
        string? launchUrl,
        CancellationToken cancellationToken = default
    ) =>
        ConfiglueDevTools.OpenBrowserAsync(
            launchUrl,
            new GodotBrowserLauncher(),
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

    private sealed class GodotBrowserLauncher : IConfiglueDevToolsBrowserLauncher
    {
        public Task OpenAsync(string url, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            global::Godot.OS.ShellOpen(url);
            return Task.CompletedTask;
        }
    }
}
