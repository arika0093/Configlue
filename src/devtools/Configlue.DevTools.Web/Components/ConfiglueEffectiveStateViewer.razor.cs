using BlazorMonaco;
using BlazorMonaco.Editor;
using Configlue.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Configlue.DevTools.Web;

/// <summary>
/// BlazorMonaco-based read-only viewer for the effective Configlue state as
/// canonical JSON with provenance overlays and schema diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Monaco runs browser-side; the Configlue runtime stays on
/// the Interactive Server side and only compact state/details metadata crosses
/// the circuit (initial JSON on load, minimal edits plus decoration deltas on
/// change). The runtime object graph is never mirrored to the browser.
/// </para>
/// <para>
/// BlazorMonaco owns editor lifecycle, values, minimal text edits, and
/// decorations. The narrow <see cref="ConfiglueMonacoBridge"/> covers only the
/// Monaco APIs BlazorMonaco does not wrap cleanly (JSON language-service schema
/// setup, hover content, inlay source labels, server-side markers).
/// </para>
/// <para>Read-only viewing only; semantic editing is tracked separately (#248).</para>
/// </remarks>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
public sealed partial class ConfiglueEffectiveStateViewer<TModel> : ComponentBase, IDisposable
{
    private StandaloneCodeEditor? _editor;
    private IDisposable? _subscription;
    private ConfiglueModelSchema? _schema;
    private ConfiglueViewerSchemaSetup? _schemaSetup;
    private ConfiglueViewerDocument? _document;
    private readonly string _editorId = Guid.NewGuid().ToString("N");
    private string[] _decorationIds = [];
    private bool _editorReady;
    private bool _schemaMissing;
    private bool _schemaConfigured;
    private long _generation;
    private int _disposed;

    /// <summary>The JS runtime for the narrow Monaco bridge (unwrapped APIs only).</summary>
    [Inject]
    public IJSRuntime Js { get; set; } = default!;

    /// <summary>The service provider used for optional diagnostics subscription.</summary>
    [Inject]
    public IServiceProvider Services { get; set; } = default!;

    /// <summary>The live state to view. Already constructed; never rediscovered.</summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyState<TModel> State { get; set; } = default!;

    /// <summary>Viewer projection options (naming policy, indent).</summary>
    [Parameter]
    public ConfiglueDevToolsViewerOptions? ViewerOptions { get; set; }

    /// <summary>The state-name identity for <c>(TModel, StateName)</c>.</summary>
    /// <remarks>
    /// Feeds the explicit Monaco document URI
    /// (<c>configlue://states/{modelId}/{stateName}</c>) so schema
    /// <c>fileMatch</c> and runtime markers target the actual editor model.
    /// </remarks>
    [Parameter]
    public string StateName { get; set; } = string.Empty;

    /// <summary>Editor height CSS value. Defaults to 480px.</summary>
    [Parameter]
    public string Height { get; set; } = "480px";

    /// <summary>The current viewer document, if loaded.</summary>
    public ConfiglueViewerDocument? CurrentDocument => _document;

    /// <summary>Whether the initial document is loaded.</summary>
    public bool IsLoaded => _document is not null;

    /// <summary>
    /// Internal test hook simulating Monaco readiness without a browser.
    /// </summary>
    internal Task SimulateEditorInitForTestsAsync() => HandleEditorInitAsync();

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(State);
        if (!ConfiglueModelSchemaCatalog.TryGet(typeof(TModel), out var schema) || schema is null)
        {
            _schemaMissing = true;
            return;
        }

        _schema = schema;
        _schemaSetup = ConfiglueDevToolsViewerProjection.BuildSchemaSetup(schema, ViewerOptions);
        // Effective-value changes drive refreshes through OnChange. Shadowed reloads leave
        // the value (and therefore decorations) unchanged, so no refresh is needed; detailed
        // timelines are available through ILogger and ActivitySource/Meter listeners.
        _subscription = State.OnChange(OnStateChanged);

        await LoadAsync(Interlocked.Increment(ref _generation)).ConfigureAwait(true);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && _editorReady && _document is not null)
        {
            await ApplyOverlaysAsync().ConfigureAwait(true);
        }

        await base.OnAfterRenderAsync(firstRender).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _subscription?.Dispose();
        _subscription = null;
        Interlocked.Increment(ref _generation);
        ClearBrowserDocument();
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
            ReadOnly = true,
            AutomaticLayout = true,
            Minimap = new EditorMinimapOptions { Enabled = false },
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

    private void OnStateChanged(TModel value)
    {
        _ = value;
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        _ = InvokeAsync(() => LoadAsync(generation));
    }

    private async Task LoadAsync(long generation)
    {
        if (!IsCurrent(generation) || _schema is null)
        {
            return;
        }

        try
        {
            var snapshot = await ReadSnapshotAsync().ConfigureAwait(true);
            if (!IsCurrent(generation))
            {
                return;
            }

            var previous = _document;
            var next = ConfiglueDevToolsViewerProjection.BuildDocument(
                snapshot.Value,
                snapshot.Details,
                _schema,
                ViewerOptions,
                (previous?.DocumentVersion ?? 0) + 1
            );
            if (
                previous is not null
                && string.Equals(previous.Json, next.Json, StringComparison.Ordinal)
                && ConfiglueDevToolsViewerProjection.AreOverlaysEqual(previous, next)
            )
            {
                // Unchanged refresh (for example a shadowed reload): no circuit traffic.
                return;
            }

            _document = next;
            if (_editorReady && _editor is not null)
            {
                if (previous is null)
                {
                    await _editor.SetValue(next.Json).ConfigureAwait(true);
                }
                else
                {
                    await ApplyTextEditsAsync(previous.Json, next.Json).ConfigureAwait(true);
                }

                await ApplyOverlaysAsync().ConfigureAwait(true);
            }

            StateHasChanged();
        }
        catch (Exception)
        {
            // Watcher-driven reload failures never replace the rendered value.
            if (IsCurrent(generation))
            {
                StateHasChanged();
            }
        }
    }

    private async ValueTask<StateSnapshot<TModel>> ReadSnapshotAsync()
    {
        if (State is IConfiglueStateSnapshotRuntime<TModel> runtime)
        {
            // One consistent resolution: value plus details transport together.
            return await runtime.GetSnapshotAsync().ConfigureAwait(false);
        }

        var value = await State.GetValueAsync().ConfigureAwait(false);
        ConfiglueDetailsSnapshot? details = null;
        if (State is IConfiglueDetailsRuntime advanced)
        {
            details = await advanced.GetDetailsSnapshotAsync().ConfigureAwait(false);
        }

        return new StateSnapshot<TModel>(value, details);
    }

    private async Task ApplyTextEditsAsync(string previousJson, string nextJson)
    {
        if (_editor is null)
        {
            return;
        }

        var edits = ConfiglueDevToolsViewerProjection.ComputeTextEdits(previousJson, nextJson);
        if (edits.Count == 0)
        {
            return;
        }

        try
        {
            var model = await _editor.GetModel().ConfigureAwait(true);
            var operations = edits
                .Select(static edit => new IdentifiedSingleEditOperation
                {
                    Range = new BlazorMonaco.Range
                    {
                        StartLineNumber = edit.Range.StartLineNumber,
                        StartColumn = edit.Range.StartColumn,
                        EndLineNumber = edit.Range.EndLineNumber,
                        EndColumn = edit.Range.EndColumn,
                    },
                    Text = edit.Text,
                    ForceMoveMarkers = true,
                })
                .ToList();
            await model.ApplyEdits(operations, false).ConfigureAwait(true);
        }
        catch (JSDisconnectedException exception)
        {
            // Circuit tearing down; the next load replays the full document.
            _ = exception;
        }
        catch (Exception exception)
        {
            _ = exception;
            try
            {
                await _editor.SetValue(nextJson).ConfigureAwait(true);
            }
            catch (Exception fallback)
            {
                // Best effort only; the next refresh retries.
                _ = fallback;
            }
        }
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
            // Overlays are best-effort UI; failures never replace the rendered value.
            // This also absorbs test-harness JS stubs that return no decoration ids.
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

        // The document key is the explicit editor model URI, never the schema
        // URI and never the anonymous model URI.
        var key = ConfiglueDevToolsViewerProjection.BuildDocumentUri(
            _schemaSetup.ModelId,
            StateName
        );
        try
        {
            // Bind the BlazorMonaco model to the document URI first so schema
            // fileMatch and marker targeting match the real model.
            await ConfiglueMonacoBridge
                .EnsureDocumentModelAsync(Js, _editorId, key, "json", _document.Json)
                .ConfigureAwait(true);
            if (!_schemaConfigured)
            {
                // Configured once per model/schema; never per keystroke.
                await ConfiglueMonacoBridge
                    .ConfigureJsonSchemaAsync(Js, _schemaSetup, key)
                    .ConfigureAwait(true);
                _schemaConfigured = true;
            }

            await ConfiglueMonacoBridge
                .SetHoverDataAsync(Js, key, _document.Hovers, _document.MemberRanges)
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
            // Overlays are best-effort UI; failures never replace the rendered value.
            _ = exception;
        }
    }

    private void ClearBrowserDocument()
    {
        if (_schemaSetup is null)
        {
            return;
        }

        try
        {
            var key = ConfiglueDevToolsViewerProjection.BuildDocumentUri(
                _schemaSetup.ModelId,
                StateName
            );
            // Best effort: the circuit may already be gone. Selection changes
            // recreate the keyed editor subtree, so stale overlays can never
            // leak into another state's document.
            _ = ConfiglueMonacoBridge.ClearDocumentAsync(Js, key);
        }
        catch (Exception exception)
        {
            _ = exception;
        }
    }

    private bool IsCurrent(long generation) =>
        Volatile.Read(ref _disposed) == 0 && generation == Interlocked.Read(ref _generation);
}
