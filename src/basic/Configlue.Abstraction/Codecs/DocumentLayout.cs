namespace Configlue.Codecs;

/// <summary>The persisted document structure used by the JSON and YAML state codecs.</summary>
public enum DocumentLayout
{
    /// <summary>Stores the schema version inline, such as <c>{ "$version": 1, ... }</c>. No model ID is stored.</summary>
    Simple = 0,

    /// <summary>Stores metadata and payload in a <c>$configlue</c>/<c>$value</c> envelope.</summary>
    Detailed = 1,
}

/// <summary>Customizes the persisted document structure for the JSON and YAML state codecs.</summary>
public sealed class DocumentLayoutOptions
{
    /// <summary>The layout used when writing documents. Reads accept both layouts.</summary>
    public DocumentLayout Layout { get; init; } = DocumentLayout.Simple;

    /// <summary>The property that carries the schema version in the simple layout.</summary>
    public string VersionProperty { get; init; } = "$version";

    /// <summary>Legacy version property names accepted when reading simple documents.</summary>
    public IReadOnlyList<string> FallbackVersionProperties { get; init; } = ["Version"];

    /// <summary>An optional model ID attributed to versions read from simple documents, which store no ID.</summary>
    /// <remarks>Used for historical schema dispatch when adopting payloads written by other serializers. Never written.</remarks>
    public string? ModelId { get; init; }
}
