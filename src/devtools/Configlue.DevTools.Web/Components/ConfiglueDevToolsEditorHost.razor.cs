using Configlue.DevTools;
using Microsoft.AspNetCore.Components;

namespace Configlue.DevTools.Web;

/// <summary>
/// Non-generic dispatcher that renders the live semantic editor for the selected state.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Resolves the bound entry from the live registry and
/// renders the matching closed <c>ConfiglueEffectiveStateEditor&lt;TModel&gt;</c>.
/// The parent shell keys this component by selection, so changing the selection
/// disposes the previous editor (and its owned server-side edit session) and
/// opens a fresh session for the next state. A draft can never cross-commit to
/// another state.
/// </para>
/// </remarks>
public sealed partial class ConfiglueDevToolsEditorHost : ComponentBase
{
    private RenderFragment? _editor;
    private string? _resolvedIdentity;

    /// <summary>The live registry bound before the host started. Never rediscovered.</summary>
    [Inject]
    public ConfiglueDevToolsRegistry Registry { get; set; } = default!;

    /// <summary>The selected model id.</summary>
    [Parameter]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>The selected state name.</summary>
    [Parameter]
    public string StateName { get; set; } = string.Empty;

    /// <summary>
    /// The resolved <c>modelId:stateName</c> identity, once bound (internal test hook).
    /// </summary>
    internal string? ResolvedIdentity => _resolvedIdentity;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        ArgumentNullException.ThrowIfNull(Registry);
        if (
            Registry.TryGet(ModelId, StateName, out var entry)
            && entry is not null
            && ConfiglueDevToolsEditorRenderer.TryCreateEditor(entry, out var fragment)
        )
        {
            _editor = fragment;
            _resolvedIdentity = string.IsNullOrEmpty(entry.Info.StateName)
                ? entry.Info.ModelId
                : $"{entry.Info.ModelId}:{entry.Info.StateName}";
            return;
        }

        _editor = null;
        _resolvedIdentity = null;
    }
}
