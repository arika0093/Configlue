#if TOOLS
using Configlue.DevTools;

namespace Configlue.DevTools.Godot;

/// <summary>
/// Editor-only plugin hook for opening the active Configlue DevTools session.
/// </summary>
/// <remarks>Compiled only when <c>TOOLS</c> is defined, so exported games never contain this integration.</remarks>
[global::Godot.Tool]
public partial class ConfiglueDevToolsEditorPlugin : global::Godot.EditorPlugin
{
    private const string MenuItemName = "Configlue/Open DevTools";

    /// <inheritdoc />
    public override void _EnterTree()
    {
        AddToolMenuItem(MenuItemName, new global::Godot.Callable(this, nameof(OnOpenDevTools)));
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        RemoveToolMenuItem(MenuItemName);
    }

    private void OnOpenDevTools()
    {
        if (
            !ConfiglueDevTools.IsEnabled
            || string.IsNullOrWhiteSpace(ConfiglueDevTools.CurrentLaunchUrl)
        )
        {
            global::Godot.GD.PushWarning(
                "Configlue DevTools is not enabled. Start the loopback DevTools host and publish its launch URL first."
            );
            return;
        }

        global::Godot.OS.ShellOpen(ConfiglueDevTools.CurrentLaunchUrl);
    }
}
#endif
