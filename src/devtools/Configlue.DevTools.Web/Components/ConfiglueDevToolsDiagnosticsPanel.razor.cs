using Configlue.DevTools;
using Microsoft.AspNetCore.Components;

namespace Configlue.DevTools.Web;

/// <summary>
/// Diagnostics/statistics tab for the development-only DevTools browser app.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Renders the existing in-process diagnostics data for the
/// selected state: cached topology/status snapshots and the bounded recent-event
/// timeline load when the tab opens; value/provenance statistics load on
/// explicit request (they perform a resolution read); the active
/// <c>Check()</c> runs only via its explicit button and never on tab open.
/// </para>
/// <para>
/// All payloads come from the redacting projection layer. Failures surface the
/// failure type only, never raw messages that could carry secret values.
/// </para>
/// </remarks>
public sealed partial class ConfiglueDevToolsDiagnosticsPanel : ComponentBase
{
    private string? _diagnosticsJson;
    private string? _eventsJson;
    private string? _statsJson;
    private string? _checkJson;
    private string? _error;
    private string _titleSuffix = string.Empty;

    /// <summary>The live registry bound before the host started. Never rediscovered.</summary>
    [Inject]
    public ConfiglueDevToolsRegistry Registry { get; set; } = default!;

    /// <summary>The selected model id.</summary>
    [Parameter]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>The selected state name.</summary>
    [Parameter]
    public string StateName { get; set; } = string.Empty;

    /// <summary>Whether the last active check completed (internal test hook).</summary>
    internal bool CheckCompleted => _checkJson is not null;

    /// <summary>Whether statistics were loaded (internal test hook).</summary>
    internal bool StatisticsLoaded => _statsJson is not null;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(Registry);
        _titleSuffix = string.IsNullOrEmpty(StateName) ? string.Empty : $":{StateName}";
        // Cached snapshots only: no source reads and never an active Check().
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        if (!TryResolve(out var entry) || entry is null)
        {
            _error = nameof(InvalidOperationException);
            return;
        }

        try
        {
            _diagnosticsJson = await entry
                .GetDiagnosticsJsonAsync(CancellationToken.None)
                .ConfigureAwait(true);
            _eventsJson = await entry
                .GetEventsJsonAsync(CancellationToken.None)
                .ConfigureAwait(true);
            _error = null;
        }
        catch (Exception exception)
        {
            _error = DescribeFailure(exception);
        }
    }

    private async Task LoadStatisticsAsync()
    {
        if (!TryResolve(out var entry) || entry is null)
        {
            _error = nameof(InvalidOperationException);
            return;
        }

        try
        {
            _statsJson = await entry.GetStatsJsonAsync(CancellationToken.None).ConfigureAwait(true);
            _error = null;
        }
        catch (Exception exception)
        {
            _error = DescribeFailure(exception);
        }
    }

    private async Task RunCheckAsync()
    {
        if (!TryResolve(out var entry) || entry is null)
        {
            _error = nameof(InvalidOperationException);
            return;
        }

        try
        {
            _checkJson = await entry.RunCheckAsync(CancellationToken.None).ConfigureAwait(true);
            _error = null;
        }
        catch (Exception exception)
        {
            _error = DescribeFailure(exception);
        }
    }

    private bool TryResolve(out IConfiglueDevToolsEntry? entry) =>
        Registry.TryGet(ModelId, StateName, out entry);

    private static string DescribeFailure(Exception exception) => exception.GetType().Name;
}
