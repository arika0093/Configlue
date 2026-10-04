using Configlue.DevTools;
using Microsoft.AspNetCore.Components;

namespace Configlue.DevTools.Web;

/// <summary>
/// Active DevTools tab in the Blazor shell.
/// </summary>
public enum ConfiglueDevToolsTab
{
    /// <summary>The BlazorMonaco semantic editor for the selected state.</summary>
    Editor,

    /// <summary>Cached diagnostics/statistics plus the explicit check runner.</summary>
    Diagnostics,
}

/// <summary>
/// Interactive Server shell for the development-only DevTools browser app.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Enumerates the live <see cref="ConfiglueDevToolsRegistry"/>
/// states (including named and dynamic states), renders the BlazorMonaco
/// semantic editor for the selected state, and hosts the diagnostics tab.
/// Selecting another state recreates the editor (keyed subtree), so the owned
/// server-side edit session is disposed and a stale draft can never commit to
/// a different state.
/// </para>
/// </remarks>
public sealed partial class ConfiglueDevToolsShell : ComponentBase
{
    private const char SelectionSeparator = '\u001F';

    private IReadOnlyList<ConfiglueDevToolsStateInfo> _states = [];
    private string _selectedKey = string.Empty;
    private string _selectedModelId = string.Empty;
    private string _selectedStateName = string.Empty;
    private ConfiglueDevToolsTab _tab = ConfiglueDevToolsTab.Editor;

    /// <summary>The live registry bound before the host started. Never rediscovered.</summary>
    [Inject]
    public ConfiglueDevToolsRegistry Registry { get; set; } = default!;

    /// <summary>The currently selected state key (internal test hook).</summary>
    internal string SelectedKey => _selectedKey;

    /// <summary>The active tab.</summary>
    internal ConfiglueDevToolsTab ActiveTab => _tab;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        ArgumentNullException.ThrowIfNull(Registry);
        RefreshStates();
    }

    internal static string SelectionKey(ConfiglueDevToolsStateInfo info) =>
        info.ModelId + SelectionSeparator + info.StateName;

    private void RefreshStates()
    {
        _states = Registry.States;
        if (_states.Count == 0)
        {
            _selectedKey = string.Empty;
            _selectedModelId = string.Empty;
            _selectedStateName = string.Empty;
            return;
        }

        var stillBound = _states.Any(info => SelectionKey(info) == _selectedKey);
        var selected = stillBound
            ? _states.First(info => SelectionKey(info) == _selectedKey)
            : _states[0];
        _selectedKey = SelectionKey(selected);
        _selectedModelId = selected.ModelId;
        _selectedStateName = selected.StateName;
    }

    private void OnSelectionChanged(ChangeEventArgs args)
    {
        var key = args.Value?.ToString() ?? string.Empty;
        var match = _states.FirstOrDefault(info => SelectionKey(info) == key);
        if (match is null)
        {
            return;
        }

        // Switching selection recreates the keyed editor subtree: the previous
        // edit session is disposed and the next state opens a fresh session.
        _selectedKey = key;
        _selectedModelId = match.ModelId;
        _selectedStateName = match.StateName;
    }

    private void ShowTab(ConfiglueDevToolsTab tab) => _tab = tab;
}
