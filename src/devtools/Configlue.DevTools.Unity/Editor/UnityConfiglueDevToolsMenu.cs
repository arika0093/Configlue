#if UNITY_EDITOR
using Configlue.DevTools;
using UnityEditor;
using UnityEngine;

namespace Configlue.DevTools.Unity;

/// <summary>
/// Editor-only menu for opening the active Configlue DevTools session in the system browser.
/// </summary>
/// <remarks>Compiled only when <c>UNITY_EDITOR</c> is defined, so player builds never contain this menu.</remarks>
public static class UnityConfiglueDevToolsMenu
{
    /// <summary>Opens the active DevTools session (<c>Tools &gt; Configlue &gt; Open DevTools</c>).</summary>
    [MenuItem("Tools/Configlue/Open DevTools")]
    public static void OpenDevTools()
    {
        if (
            !ConfiglueDevTools.IsEnabled
            || string.IsNullOrWhiteSpace(ConfiglueDevTools.CurrentLaunchUrl)
        )
        {
            Debug.LogWarning(
                "Configlue DevTools is not enabled. Start the loopback DevTools host and publish its launch URL first."
            );
            return;
        }

        Application.OpenURL(ConfiglueDevTools.CurrentLaunchUrl);
    }
}
#endif
