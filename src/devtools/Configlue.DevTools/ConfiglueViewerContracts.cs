using System.Text.Json;

namespace Configlue.DevTools;

/// <summary>
/// A 1-based Monaco editor range (line/column, inclusive start, exclusive end).
/// </summary>
/// <remarks>
/// Development-only viewer metadata. Positions track the canonical JSON projection
/// emitted alongside the document; no full-document rescan is needed to reuse them.
/// </remarks>
public readonly record struct ConfiglueViewerRange(
    int StartLineNumber,
    int StartColumn,
    int EndLineNumber,
    int EndColumn
)
{
    /// <summary>Whether the range holds plausible 1-based Monaco coordinates.</summary>
    public bool IsValid =>
        StartLineNumber >= 1
        && StartColumn >= 1
        && EndLineNumber >= StartLineNumber
        && (EndLineNumber > StartLineNumber || EndColumn >= StartColumn);
}

/// <summary>One member's document spans in the canonical JSON projection.</summary>
/// <remarks>
/// Development-only viewer metadata. <see cref="MemberPath"/> uses generated
/// member names (<c>Database.Password</c>, collection elements as
/// <c>Tokens[0]</c>); <see cref="WireName"/> is the naming-policy-aware JSON key.
/// </remarks>
public sealed record ConfiglueViewerMemberRange(
    string MemberPath,
    string WireName,
    ConfiglueViewerRange NameRange,
    ConfiglueViewerRange ValueRange,
    bool IsSecret
);

/// <summary>Provenance overlay kinds. Restrained by design; never text edits.</summary>
/// <remarks>Development-only viewer metadata.</remarks>
public enum ConfiglueViewerDecorationKind
{
    /// <summary>The effective/current contribution.</summary>
    Effective,

    /// <summary>Read-only or otherwise non-editable effective value.</summary>
    ReadOnly,

    /// <summary>Sensitive member redacted at the viewer layer.</summary>
    Secret,

    /// <summary>Invalid contribution or failed validation.</summary>
    Invalid,

    /// <summary>Compact source identity label (inlay/hover, not strong colors).</summary>
    SourceLabel,
}

/// <summary>One Monaco decoration/inlay/glyph overlay for a member range.</summary>
/// <remarks>
/// Development-only viewer metadata. Overlays only; the JSON document text is
/// never modified to carry provenance. Secret values never appear in
/// <see cref="Label"/> or any other string.
/// </remarks>
public sealed record ConfiglueViewerDecoration(
    string MemberPath,
    ConfiglueViewerRange Range,
    ConfiglueViewerDecorationKind Kind,
    string Label,
    string CssClass
);

/// <summary>One source's contribution to a member for hover display.</summary>
/// <remarks>
/// Development-only viewer metadata. Values are safe display strings only;
/// secret contributions report presence, never plaintext.
/// </remarks>
public sealed record ConfiglueViewerContribution(
    string SourceKey,
    string DisplayName,
    string Kind,
    string State,
    bool IsEffective,
    bool IsShadowed,
    string? Locator
);

/// <summary>Hover/explain payload for one member.</summary>
/// <remarks>
/// Development-only viewer metadata. Explains effective and shadowed
/// contributions without a separate public inspection API. Secrets expose safe
/// state only.
/// </remarks>
public sealed record ConfiglueViewerHover(
    string MemberPath,
    string Title,
    string Markdown,
    string? EffectiveSource,
    string Editability,
    bool IsSecret,
    IReadOnlyList<ConfiglueViewerContribution> Contributions,
    string? Locator
);

/// <summary>Server-side validation marker severity for Monaco.</summary>
/// <remarks>Development-only viewer metadata.</remarks>
public enum ConfiglueViewerMarkerSeverity
{
    /// <summary>Hint.</summary>
    Hint,

    /// <summary>Information.</summary>
    Info,

    /// <summary>Warning.</summary>
    Warning,

    /// <summary>Error.</summary>
    Error,
}

/// <summary>One server-side (Configlue runtime) validation marker.</summary>
/// <remarks>
/// Development-only viewer metadata. Supplements Monaco browser-side JSON
/// Schema validation. Messages are redacted for sensitive members.
/// </remarks>
public sealed record ConfiglueViewerMarker(
    string MemberPath,
    ConfiglueViewerRange Range,
    ConfiglueViewerMarkerSeverity Severity,
    string Message,
    string? Source
);

/// <summary>One minimal Monaco text edit (preserves scroll/selection).</summary>
/// <remarks>Development-only viewer metadata.</remarks>
public sealed record ConfiglueViewerTextEdit(ConfiglueViewerRange Range, string Text);

/// <summary>The canonical effective-state viewer document.</summary>
/// <remarks>
/// Development-only viewer metadata. The JSON text is an effective semantic
/// projection (YAML/env/Vault project to JSON); provenance travels in
/// <see cref="Decorations"/>/<see cref="Hovers"/>/<see cref="Markers"/>, never
/// as injected comments or text edits.
/// </remarks>
public sealed record ConfiglueViewerDocument(
    string ModelId,
    int ModelVersion,
    long DocumentVersion,
    string Json,
    string SchemaUri,
    IReadOnlyList<ConfiglueViewerMemberRange> MemberRanges,
    IReadOnlyList<ConfiglueViewerDecoration> Decorations,
    IReadOnlyList<ConfiglueViewerHover> Hovers,
    IReadOnlyList<ConfiglueViewerMarker> Markers
);

/// <summary>Monaco JSON language-service setup; configured once per model/schema.</summary>
/// <remarks>
/// Development-only viewer metadata. The browser-side JSON language service is
/// configured from this schema once; no per-keystroke round-trips reproduce
/// validation Monaco performs locally.
/// </remarks>
public sealed record ConfiglueViewerSchemaSetup(
    string ModelId,
    int ModelVersion,
    string SchemaUri,
    string SchemaJson
);

/// <summary>Incremental refresh outcome for an already-loaded viewer document.</summary>
/// <remarks>
/// Development-only viewer metadata. <see cref="TextChanged"/> is false for
/// decoration-only updates, in which case <see cref="TextEdits"/> is empty and
/// no full-document payload crosses the circuit.
/// </remarks>
public sealed record ConfiglueViewerRefreshResult(
    ConfiglueViewerDocument Document,
    IReadOnlyList<ConfiglueViewerTextEdit> TextEdits,
    bool TextChanged,
    bool DecorationsChanged
);

/// <summary>Options for the effective-state viewer projection.</summary>
/// <remarks>Development-only viewer options.</remarks>
public sealed class ConfiglueDevToolsViewerOptions
{
    /// <summary>
    /// The naming policy applied to generated member names for JSON wire names,
    /// such as <see cref="JsonNamingPolicy.CamelCase"/>. Null keeps CLR names.
    /// </summary>
    public JsonNamingPolicy? NamingPolicy { get; set; }

    /// <summary>Indentation used by the canonical projection. Defaults to two spaces.</summary>
    public string Indent { get; set; } = "  ";

    /// <summary>Default options (CLR names, two-space indent).</summary>
    public static ConfiglueDevToolsViewerOptions Default { get; } = new();
}
