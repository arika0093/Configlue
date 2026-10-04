using Configlue.DevTools;
using Microsoft.JSInterop;

namespace Configlue.DevTools.Web;

/// <summary>
/// Narrow Configlue-specific JS interop bridge for Monaco APIs that BlazorMonaco
/// does not expose cleanly (browser JSON language-service schema setup, hover
/// content from viewer hovers, inlay source labels, server-side markers).
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Normal editor lifecycle, values, edits, and decorations
/// go through BlazorMonaco (<c>StandaloneCodeEditor</c>); this bridge only
/// covers the unwrapped Monaco APIs listed above and stays Configlue-specific
/// rather than becoming a general Monaco wrapper.
/// </para>
/// <para>All payloads are redacted viewer metadata; secret plaintext never crosses here.</para>
/// </remarks>
public static class ConfiglueMonacoBridge
{
    private const string Global = "configlueDevToolsMonaco";

    /// <summary>
    /// Configures the browser-side Monaco JSON language service once per
    /// model/schema. Remote schemas are never fetched; the schema is inline.
    /// Failures (for example prerender without JS) are swallowed by callers.
    /// </summary>
    public static ValueTask ConfigureJsonSchemaAsync(
        IJSRuntime js,
        ConfiglueViewerSchemaSetup setup
    )
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(setup);
        return ConfigureJsonSchemaAsync(js, setup, setup.SchemaUri);
    }

    /// <summary>
    /// Configures the browser-side Monaco JSON language service with an
    /// explicit document URI binding.
    /// </summary>
    /// <remarks>
    /// The schema <c>fileMatch</c> targets <paramref name="documentUri"/>
    /// (normally <c>configlue://states/{modelId}/{stateName}</c>), which must
    /// be the actual editor model URI bound via
    /// <see cref="EnsureDocumentModelAsync"/>. Never the anonymous model URI.
    /// </remarks>
    public static ValueTask ConfigureJsonSchemaAsync(
        IJSRuntime js,
        ConfiglueViewerSchemaSetup setup,
        string documentUri
    )
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentUri);
        return js.InvokeVoidAsync(
            $"{Global}.setJsonSchema",
            setup.SchemaUri,
            setup.SchemaJson,
            documentUri
        );
    }

    /// <summary>
    /// Binds the BlazorMonaco editor model to the explicit Configlue document
    /// URI so schema <c>fileMatch</c> and marker targeting match the real
    /// model. Swaps the anonymous model once; later calls preserve
    /// scroll/selection.
    /// </summary>
    public static ValueTask EnsureDocumentModelAsync(
        IJSRuntime js,
        string editorId,
        string documentUri,
        string language = "json",
        string fallbackJson = "{\n}"
    )
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentException.ThrowIfNullOrWhiteSpace(editorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentUri);
        return js.InvokeVoidAsync(
            $"{Global}.ensureDocumentModel",
            editorId,
            documentUri,
            language,
            fallbackJson
        );
    }

    /// <summary>
    /// Publishes hover/explain payloads for the viewer document key, together
    /// with the member value ranges used to resolve hover positions.
    /// </summary>
    public static ValueTask SetHoverDataAsync(
        IJSRuntime js,
        string documentKey,
        IReadOnlyList<ConfiglueViewerHover> hovers,
        IReadOnlyList<ConfiglueViewerMemberRange> memberRanges
    )
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        ArgumentNullException.ThrowIfNull(hovers);
        ArgumentNullException.ThrowIfNull(memberRanges);
        var payload = hovers
            .Select(static hover => new
            {
                memberPath = hover.MemberPath,
                markdown = hover.Markdown,
            })
            .ToArray();
        var ranges = memberRanges
            .Select(static range => new
            {
                memberPath = range.MemberPath,
                startLineNumber = range.ValueRange.StartLineNumber,
                startColumn = range.ValueRange.StartColumn,
                endLineNumber = range.ValueRange.EndLineNumber,
                endColumn = range.ValueRange.EndColumn,
            })
            .ToArray();
        return js.InvokeVoidAsync($"{Global}.setHoverData", documentKey, payload, ranges);
    }

    /// <summary>Publishes compact inlay source labels for the viewer document key.</summary>
    public static ValueTask SetInlayLabelsAsync(
        IJSRuntime js,
        string documentKey,
        IReadOnlyList<ConfiglueViewerDecoration> decorations
    )
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        ArgumentNullException.ThrowIfNull(decorations);
        var payload = decorations
            .Where(static decoration =>
                decoration.Kind == ConfiglueViewerDecorationKind.SourceLabel
            )
            .Select(static decoration => new
            {
                memberPath = decoration.MemberPath,
                label = decoration.Label,
                startLineNumber = decoration.Range.StartLineNumber,
                startColumn = decoration.Range.StartColumn,
                endLineNumber = decoration.Range.EndLineNumber,
                endColumn = decoration.Range.EndColumn,
            })
            .ToArray();
        return js.InvokeVoidAsync($"{Global}.setInlayLabels", documentKey, payload);
    }

    /// <summary>
    /// Surfaces server-side Configlue validation as extra Monaco markers.
    /// Browser-side JSON Schema validation still comes from the configured schema.
    /// </summary>
    public static ValueTask SetRuntimeMarkersAsync(
        IJSRuntime js,
        string documentKey,
        IReadOnlyList<ConfiglueViewerMarker> markers
    )
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        ArgumentNullException.ThrowIfNull(markers);
        var payload = markers
            .Select(static marker => new
            {
                memberPath = marker.MemberPath,
                severity = marker.Severity.ToString(),
                message = marker.Message,
                startLineNumber = marker.Range.StartLineNumber,
                startColumn = marker.Range.StartColumn,
                endLineNumber = marker.Range.EndLineNumber,
                endColumn = marker.Range.EndColumn,
            })
            .ToArray();
        return js.InvokeVoidAsync($"{Global}.setRuntimeMarkers", documentKey, payload);
    }

    /// <summary>
    /// Drops cached hover/inlay/marker payloads for a closed document and
    /// clears its markers, so selection changes and disposal never leak
    /// overlays across states.
    /// </summary>
    public static ValueTask ClearDocumentAsync(IJSRuntime js, string documentKey)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        return js.InvokeVoidAsync($"{Global}.clearDocument", documentKey);
    }
}
