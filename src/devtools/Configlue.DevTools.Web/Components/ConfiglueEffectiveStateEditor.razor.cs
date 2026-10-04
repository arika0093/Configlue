using BlazorMonaco;
using BlazorMonaco.Editor;
using Configlue.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Configlue.DevTools.Web;

/// <summary>
/// BlazorMonaco-based semantic editor for the effective Configlue state.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Monaco runs browser-side while the Configlue runtime and an
/// owned <see cref="ConfiglueDevToolsEditorSession{TModel}"/> stay on the
/// Interactive Server side. The Monaco JSON document is an effective-state
/// projection, never a storage document: typing stays browser-local behind a
/// debounce gate, drafts synchronize to the server only after the draft is quiet
/// (or on explicit Save/Diff/Validate), and persistence always flows through the
/// normal Configlue EditSession validation and write routing.
/// </para>
/// <para>
/// BlazorMonaco owns editor lifecycle and values. The narrow
/// <see cref="ConfiglueMonacoBridge"/> covers only the Monaco APIs BlazorMonaco
/// does not wrap cleanly (JSON language-service schema setup, hover content, inlay
/// source labels, server-side markers).
/// </para>
/// <para>
/// Secrets never enter the Monaco model: the transient Change-secret panel lives
/// outside the editor, empty means unchanged, and its input is cleared after
/// apply or cancel.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
public sealed partial class ConfiglueEffectiveStateEditor<TModel> : ComponentBase, IDisposable
    where TModel : IConfiglueFacadeModel<TModel>
{
    private StandaloneCodeEditor? _editor;
    private StandaloneDiffEditor? _diffEditor;
    private ConfiglueDevToolsEditorSession<TModel>? _session;
    private ConfiglueViewerSchemaSetup? _schemaSetup;
    private ConfiglueViewerDocument? _document;
    private ConfiglueDevToolsDraftThrottle _throttle = new();
    private CancellationTokenSource? _debounceCts;
    private string[] _decorationIds = [];
    private bool _editorReady;
    private bool _schemaMissing;
    private bool _schemaConfigured;
    private bool _sessionReady;
    private bool _busy;
    private bool _showDiff;
    private bool _showSecret;
    private string _secretPath = string.Empty;
    private string _secretValue = string.Empty;
    private readonly List<string> _errors = [];
    private string? _notice;
    private long _generation;
    private int _disposed;

    /// <summary>The JS runtime for the narrow Monaco bridge (unwrapped APIs only).</summary>
    [Inject]
    public IJSRuntime Js { get; set; } = default!;

    /// <summary>The live writable state to edit. Already constructed; never rediscovered.</summary>
    [Parameter]
    [EditorRequired]
    public IWritableState<TModel> State { get; set; } = default!;

    /// <summary>Viewer projection options (naming policy, indent).</summary>
    [Parameter]
    public ConfiglueDevToolsViewerOptions? ViewerOptions { get; set; }

    /// <summary>The state-name identity for <c>(TModel, StateName)</c>.</summary>
    [Parameter]
    public string StateName { get; set; } = string.Empty;

    /// <summary>Editor height CSS value. Defaults to 480px.</summary>
    [Parameter]
    public string Height { get; set; } = "480px";

    /// <summary>
    /// How long the draft must be quiet before a background synchronization.
    /// Explicit Save/Diff/Validate always synchronize immediately.
    /// </summary>
    [Parameter]
    public TimeSpan DebounceInterval { get; set; } = TimeSpan.FromMilliseconds(600);

    /// <summary>The owned server-side editing session, once opened.</summary>
    public ConfiglueDevToolsEditorSession<TModel>? Session => _session;

    /// <summary>Whether the editing session is open and Monaco holds the draft.</summary>
    public bool IsLoaded => _sessionReady && _session is not null;

    /// <summary>Number of changed member paths in the last synchronized draft.</summary>
    public int ModifiedCount => _session?.ModifiedPaths.Count ?? 0;

    /// <summary>Whether upstream changed since the current baseline.</summary>
    public bool HasUpstreamChanges => _session?.HasUpstreamChanges ?? false;

    /// <summary>Current surfaced failure lines (category-prefixed).</summary>
    public IReadOnlyList<string> CurrentErrors => _errors.AsReadOnly();

    /// <summary>Whether the transient Change-secret panel is open.</summary>
    public bool IsSecretPanelOpen => _showSecret;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(State);
        if (!ConfiglueModelSchemaCatalog.TryGet(typeof(TModel), out var schema) || schema is null)
        {
            _schemaMissing = true;
            return;
        }

        _throttle = new ConfiglueDevToolsDraftThrottle(DebounceInterval);
        try
        {
            var generation = Interlocked.Increment(ref _generation);
            var session = await ConfiglueDevToolsEditorSession<TModel>
                .OpenAsync(State, ViewerOptions, StateName, CancellationToken.None)
                .ConfigureAwait(true);
            if (!IsCurrent(generation))
            {
                session.Dispose();
                return;
            }

            _session = session;
            _schemaSetup = session.GetSchemaSetup();
            _document = session.CurrentDocument;
            _session.Changed += OnSessionChanged;
            _sessionReady = true;
        }
        catch (Exception exception)
        {
            _errors.Add(DescribeOpenFailure(exception));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _generation);
        CancelDebounce();
        if (_session is not null)
        {
            _session.Changed -= OnSessionChanged;
            _session.Dispose();
            _session = null;
        }

        GC.SuppressFinalize(this);
    }

    private StandaloneEditorConstructionOptions BuildConstructionOptions(
        StandaloneCodeEditor editor
    )
    {
        _ = editor;
        return new StandaloneEditorConstructionOptions
        {
            Language = "json",
            Value = _document?.Json ?? "{\n}",
            Theme = "vs-dark",
            ReadOnly = false,
            AutomaticLayout = true,
            Minimap = new EditorMinimapOptions { Enabled = false },
        };
    }

    private StandaloneDiffEditorConstructionOptions BuildDiffOptions(StandaloneDiffEditor editor)
    {
        _ = editor;
        return new StandaloneDiffEditorConstructionOptions
        {
            Theme = "vs-dark",
            AutomaticLayout = true,
            OriginalEditable = false,
            RenderSideBySide = true,
        };
    }

    private async Task HandleEditorInitAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _editorReady = true;
        if (_document is not null)
        {
            await ApplyOverlaysAsync().ConfigureAwait(true);
        }
    }

    private async Task HandleDiffInitAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await RefreshDiffModelsAsync().ConfigureAwait(true);
    }

    private void HandleContentChanged(ModelContentChangedEvent change)
    {
        _ = change;
        if (Volatile.Read(ref _disposed) != 0 || _session is null)
        {
            return;
        }

        // Browser-local keystroke: record and debounce. No server traffic here.
        _throttle.NoteEdit(DateTimeOffset.UtcNow);
        RestartDebounce();
    }

    private void RestartDebounce()
    {
        CancelDebounce();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        var generation = Interlocked.Read(ref _generation);
        _ = DebouncedSyncAsync(cts, generation);
    }

    private void CancelDebounce()
    {
        var cts = Interlocked.Exchange(ref _debounceCts, null);
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception)
            {
                // Cancellation is best-effort UI plumbing.
            }

            cts.Dispose();
        }
    }

    private async Task DebouncedSyncAsync(CancellationTokenSource cts, long generation)
    {
        try
        {
            await Task.Delay(_throttle.DebounceInterval, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!IsCurrent(generation))
        {
            return;
        }

        await InvokeAsync(() => SyncNowAsync(explicitRequest: false)).ConfigureAwait(true);
    }

    private async Task SyncNowAsync(bool explicitRequest)
    {
        if (_session is null || _editor is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!explicitRequest && !_throttle.ShouldSync(now, explicitRequest: false))
        {
            return;
        }

        string text;
        try
        {
            text = await _editor.GetValue().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Editor tearing down; the next explicit action retries.
            return;
        }

        _throttle.MarkSent(now);
        _busy = true;
        try
        {
            var result = await _session
                .SyncDraftAsync(text, CancellationToken.None)
                .ConfigureAwait(true);
            ApplySyncResult(result, noticeOnClean: explicitRequest);
            if (_showDiff)
            {
                await RefreshDiffModelsAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _busy = false;
        }

        StateHasChanged();
    }

    private async Task SaveAsync()
    {
        if (_session is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _busy = true;
        try
        {
            await SyncNowAsync(explicitRequest: true).ConfigureAwait(true);
            var result = await _session.CommitAsync(CancellationToken.None).ConfigureAwait(true);
            if (result.Committed)
            {
                _errors.Clear();
                _notice = DescribeReceipt(result.Receipt);
                await ReloadEditorFromSessionAsync().ConfigureAwait(true);
            }
            else
            {
                ApplyFailure(result.Category, result.Errors);
            }
        }
        catch (Exception exception)
        {
            ApplyFailure(
                ConfiglueDevToolsEditorSession<TModel>.Classify(exception),
                [exception.Message]
            );
        }
        finally
        {
            _busy = false;
        }

        StateHasChanged();
    }

    private async Task DiscardAsync()
    {
        if (_session is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _busy = true;
        try
        {
            _session.DiscardChanges();
            _errors.Clear();
            _notice = "Draft discarded; restored the session-start value.";
            await ReloadEditorFromSessionAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ApplyFailure(
                ConfiglueDevToolsEditorSession<TModel>.Classify(exception),
                [exception.Message]
            );
        }
        finally
        {
            _busy = false;
        }

        StateHasChanged();
    }

    private async Task ValidateAsync()
    {
        if (_session is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _busy = true;
        try
        {
            await SyncNowAsync(explicitRequest: true).ConfigureAwait(true);
            var preview = await _session.ValidateAsync(CancellationToken.None).ConfigureAwait(true);
            if (preview.Success)
            {
                _errors.Clear();
                _notice = DescribePreview(preview);
            }
            else
            {
                ApplyFailure(preview.Category, preview.Errors);
            }
        }
        catch (Exception exception)
        {
            ApplyFailure(
                ConfiglueDevToolsEditorSession<TModel>.Classify(exception),
                [exception.Message]
            );
        }
        finally
        {
            _busy = false;
        }

        StateHasChanged();
    }

    private async Task RebaseAsync()
    {
        if (_session is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _busy = true;
        try
        {
            var result = await _session.RebaseAsync(CancellationToken.None).ConfigureAwait(true);
            if (result.Success)
            {
                _errors.Clear();
                _notice = "Rebased onto the latest upstream.";
                await ReloadEditorFromSessionAsync().ConfigureAwait(true);
            }
            else
            {
                ApplyFailure(result.Category, result.Errors);
            }
        }
        catch (Exception exception)
        {
            ApplyFailure(
                ConfiglueDevToolsEditorSession<TModel>.Classify(exception),
                [exception.Message]
            );
        }
        finally
        {
            _busy = false;
        }

        StateHasChanged();
    }

    private async Task ToggleDiffAsync()
    {
        _showDiff = !_showDiff;
        if (_showDiff)
        {
            // The diff editor initializes its models on init.
            StateHasChanged();
            return;
        }

        StateHasChanged();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private void ToggleSecretPanel()
    {
        _showSecret = !_showSecret;
        if (!_showSecret)
        {
            ClearSecretInputs();
        }
    }

    private void CancelSecret()
    {
        ClearSecretInputs();
        _showSecret = false;
    }

    private async Task ApplySecretAsync()
    {
        if (_session is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _busy = true;
        try
        {
            var result = await _session
                .ApplySecretAsync(_secretPath, _secretValue, CancellationToken.None)
                .ConfigureAwait(true);
            if (result.Success)
            {
                _errors.Clear();
                _notice =
                    result.ModifiedCount == 0
                        ? "Secret unchanged."
                        : $"Secret '{_secretPath}' staged; Save to commit.";
            }
            else
            {
                ApplyFailure(result.Category, result.Errors);
            }
        }
        catch (Exception exception)
        {
            ApplyFailure(
                ConfiglueDevToolsEditorSession<TModel>.Classify(exception),
                [exception.Message]
            );
        }
        finally
        {
            // Plaintext is cleared as aggressively as practical after every
            // apply or cancel; it never survives in circuit state.
            ClearSecretInputs();
            _busy = false;
        }

        StateHasChanged();
    }

    private void ClearSecretInputs()
    {
        _secretValue = string.Empty;
        _secretPath = string.Empty;
    }

    private void OnSessionChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        _ = InvokeAsync(() => FollowUpstreamAsync(generation));
    }

    private async Task FollowUpstreamAsync(long generation)
    {
        if (_session is null || !IsCurrent(generation))
        {
            return;
        }

        try
        {
            // Clean sessions follow the existing automatic rebase behavior;
            // dirty sessions are never silently overwritten.
            if (await _session.RefreshAsync(CancellationToken.None).ConfigureAwait(true))
            {
                _document = _session.CurrentDocument;
                _errors.Clear();
                _notice = "Followed upstream (clean session rebased).";
                if (_editorReady && _editor is not null && !_showDiff)
                {
                    await ReloadEditorFromSessionAsync().ConfigureAwait(true);
                }
            }
            else if (_session.HasUpstreamChanges)
            {
                _notice = "Upstream changed — review, then Rebase or Discard.";
            }
        }
        catch (Exception)
        {
            // Watcher-driven follow failures never replace the rendered draft.
        }

        StateHasChanged();
    }

    private void ApplySyncResult(ConfiglueEditorSyncResult result, bool noticeOnClean)
    {
        if (result.Success)
        {
            _errors.Clear();
            if (noticeOnClean && !result.HasChanges)
            {
                _notice = "Draft matches the effective state; nothing to save.";
            }
            else if (result.HasChanges)
            {
                _notice = null;
            }
        }
        else
        {
            ApplyFailure(result.Category, result.Errors);
        }
    }

    private void ApplyFailure(ConfiglueEditorFailureCategory category, IReadOnlyList<string> errors)
    {
        _errors.Clear();
        if (errors.Count == 0)
        {
            _errors.Add($"[{category}] The operation failed without details.");
            return;
        }

        foreach (var error in errors)
        {
            _errors.Add($"[{category}] {error}");
        }

        _notice = null;
    }

    private async Task ReloadEditorFromSessionAsync()
    {
        if (_session is null || _editor is null)
        {
            return;
        }

        _document = _session.CurrentDocument;
        try
        {
            await _editor.SetValue(_session.BuildCanonicalDraftJson()).ConfigureAwait(true);
            await ApplyOverlaysAsync().ConfigureAwait(true);
            if (_showDiff)
            {
                await RefreshDiffModelsAsync().ConfigureAwait(true);
            }
        }
        catch (JSDisconnectedException exception)
        {
            // Circuit tearing down; the next load replays the full document.
            _ = exception;
        }
        catch (Exception exception)
        {
            _ = exception;
        }
    }

    private async Task RefreshDiffModelsAsync()
    {
        if (_diffEditor is null || _session is null)
        {
            return;
        }

        try
        {
            // The diff is a developer convenience only; commit semantics remain
            // Configlue semantic patch/write routing. Values flow through the
            // inner editors so no extra text-model protocol is introduced.
            var modified = await ReadDraftTextAsync().ConfigureAwait(true);
            await _diffEditor
                .OriginalEditor.SetValue(_session.SessionStartDocument.Json)
                .ConfigureAwait(true);
            await _diffEditor.ModifiedEditor.SetValue(modified).ConfigureAwait(true);
        }
        catch (JSDisconnectedException exception)
        {
            _ = exception;
        }
        catch (Exception exception)
        {
            _ = exception;
        }
    }

    private async Task<string> ReadDraftTextAsync()
    {
        if (_session is null)
        {
            return string.Empty;
        }

        if (_editor is not null)
        {
            try
            {
                return await _editor.GetValue().ConfigureAwait(true);
            }
            catch (Exception)
            {
                // The main editor may be unmounted while the diff is shown.
            }
        }

        return _session.BuildCanonicalDraftJson();
    }

    private async Task ApplyOverlaysAsync()
    {
        if (_editor is null || _document is null)
        {
            return;
        }

        await ApplyDecorationsAsync().ConfigureAwait(true);
        await ApplyBridgeOverlaysAsync().ConfigureAwait(true);
    }

    private async Task ApplyDecorationsAsync()
    {
        if (_editor is null || _document is null)
        {
            return;
        }

        try
        {
            var decorations = _document
                .Decorations.Where(static decoration =>
                    decoration.Kind != ConfiglueViewerDecorationKind.SourceLabel
                )
                .Select(static decoration => new ModelDeltaDecoration
                {
                    Range = new BlazorMonaco.Range
                    {
                        StartLineNumber = decoration.Range.StartLineNumber,
                        StartColumn = decoration.Range.StartColumn,
                        EndLineNumber = decoration.Range.EndLineNumber,
                        EndColumn = decoration.Range.EndColumn,
                    },
                    Options = ForKind(decoration),
                })
                .ToArray();
            var ids = await _editor
                .DeltaDecorations(_decorationIds, decorations)
                .ConfigureAwait(true);
            _decorationIds = ids ?? [];
        }
        catch (JSDisconnectedException exception)
        {
            _ = exception;
        }
        catch (Exception exception)
        {
            // Overlays are best-effort UI; failures never replace the rendered draft.
            _ = exception;
        }

        static ModelDecorationOptions ForKind(ConfiglueViewerDecoration decoration) =>
            decoration.Kind switch
            {
                ConfiglueViewerDecorationKind.Secret => new ModelDecorationOptions
                {
                    ClassName = "configlue-secret",
                    GlyphMarginClassName = "configlue-glyph-secret",
                },
                ConfiglueViewerDecorationKind.ReadOnly => new ModelDecorationOptions
                {
                    ClassName = "configlue-muted",
                    GlyphMarginClassName = "configlue-glyph-readonly",
                },
                ConfiglueViewerDecorationKind.Invalid => new ModelDecorationOptions
                {
                    ClassName = "configlue-invalid",
                    GlyphMarginClassName = "configlue-glyph-invalid",
                },
                _ => new ModelDecorationOptions { ClassName = "configlue-effective" },
            };
    }

    private async Task ApplyBridgeOverlaysAsync()
    {
        if (_document is null || _schemaSetup is null)
        {
            return;
        }

        var key = _schemaSetup.SchemaUri;
        try
        {
            if (!_schemaConfigured)
            {
                // Configured once per model/schema; never per keystroke. The
                // browser-side JSON language service stays advisory; server
                // validation is authoritative on sync/commit.
                await ConfiglueMonacoBridge
                    .ConfigureJsonSchemaAsync(Js, _schemaSetup)
                    .ConfigureAwait(true);
                _schemaConfigured = true;
            }

            await ConfiglueMonacoBridge
                .SetHoverDataAsync(Js, key, _document.Hovers)
                .ConfigureAwait(true);
            await ConfiglueMonacoBridge
                .SetInlayLabelsAsync(Js, key, _document.Decorations)
                .ConfigureAwait(true);
            await ConfiglueMonacoBridge
                .SetRuntimeMarkersAsync(Js, key, _document.Markers)
                .ConfigureAwait(true);
        }
        catch (JSDisconnectedException exception)
        {
            _ = exception;
        }
        catch (Exception exception)
        {
            // Overlays are best-effort UI; failures never replace the rendered draft.
            _ = exception;
        }
    }

    private static string DescribePreview(ConfiglueEditorPreviewResult preview)
    {
        if (!preview.PreviewAvailable)
        {
            return "No preview available from this state; commit-time validation still applies.";
        }

        if (preview.IsEmpty)
        {
            return "No changes: nothing to write.";
        }

        return $"Dry-run ok: {preview.PhysicalWriteCount} physical write(s), atomic: {preview.IsAtomic}.";
    }

    private static string DescribeReceipt(ConfiglueEditorWriteReceipt? receipt)
    {
        if (receipt is null)
        {
            return "Saved.";
        }

        if (receipt.Sources.Count == 0)
        {
            return "Saved (no writes required).";
        }

        var sources = string.Join(
            ", ",
            receipt.Sources.Select(static source =>
                string.IsNullOrEmpty(source.Revision)
                    ? source.SourceId
                    : $"{source.SourceId}@{source.Revision}"
            )
        );
        return $"Saved to {receipt.Sources.Count} source(s) [{sources}], physical writes: {receipt.PhysicalWriteCount}, atomic: {receipt.IsAtomic}.";
    }

    private static string DescribeOpenFailure(Exception exception) =>
        exception switch
        {
            InvalidOperationException invalid =>
                $"[Invalidated] Could not open the editing session: {invalid.Message}",
            _ => $"[Invalidated] Could not open the editing session: {exception.Message}",
        };

    private bool IsCurrent(long generation) =>
        Volatile.Read(ref _disposed) == 0 && generation == Interlocked.Read(ref _generation);
}
