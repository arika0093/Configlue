using Configlue.DevTools;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Configlue.DevTools.Web;

/// <summary>
/// DevTools-only dispatcher that renders the live semantic editor for one selected state.
/// </summary>
/// <remarks>
/// Internal factory logic: generic component dispatch is awkward from
/// Razor, so the dispatcher resolves the bound entry and renders the matching
/// closed <c>ConfiglueEffectiveStateEditor&lt;TModel&gt;</c> for it. No new
/// public inspection API is introduced; resolution uses only the already-bound
/// live registry.
/// </remarks>
internal static class ConfiglueDevToolsEditorRenderer
{
    internal static bool TryCreateEditor(IConfiglueDevToolsEntry entry, out RenderFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RenderFragment? built = null;
        try
        {
            var editorType = typeof(ConfiglueEffectiveStateEditor<>).MakeGenericType(
                entry.ModelType
            );
            var stateProperty = editorType.GetProperty("State");
            var state = entry.UntypedState;
            if (stateProperty is null || !stateProperty.PropertyType.IsInstanceOfType(state))
            {
                fragment = static _ => { };
                return false;
            }

            var stateName = entry.Info.StateName;
            built = builder =>
            {
                builder.OpenComponent(0, editorType);
                builder.AddAttribute(1, "State", state);
                builder.AddAttribute(2, "StateName", stateName);
                builder.CloseComponent();
            };
        }
        catch (ArgumentException)
        {
            fragment = static _ => { };
            return false;
        }

        fragment = built;
        return true;
    }
}
