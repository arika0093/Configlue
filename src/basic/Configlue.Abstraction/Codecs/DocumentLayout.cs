namespace Configlue.Codecs;

/// <summary>The persisted document structure used by the JSON and YAML state codecs.</summary>
/// <remarks>Provider codec option.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public enum DocumentLayout
{
    /// <summary>Stores the schema version inline, such as <c>{ "$version": 1, ... }</c>. No model ID is stored.</summary>
    Simple = 0,

    /// <summary>Stores metadata and payload in a <c>$configlue</c>/<c>$value</c> envelope.</summary>
    Detailed = 1,
}

/// <summary>Customizes the persisted document structure for the JSON and YAML state codecs.</summary>
/// <remarks>Provider codec option.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class DocumentLayoutOptions
{
    /// <summary>The layout used when writing documents. Reads accept both layouts.</summary>
    public DocumentLayout Layout { get; init; } = DocumentLayout.Simple;

    /// <summary>The property that carries the schema version in the simple layout.</summary>
    /// <remarks>Canonical schema metadata. Only this name is recognized unless <see cref="FallbackVersionProperties"/> opts into legacy names.</remarks>
    public string VersionProperty { get; init; } = "$version";

    /// <summary>Additional version property names accepted when reading simple documents.</summary>
    /// <remarks>Empty by default. Configure explicitly to adopt legacy payloads that store the schema version under a different name, such as <c>Version</c>. Ordinary model members with those names are otherwise treated as payload data.</remarks>
    public IReadOnlyList<string> FallbackVersionProperties { get; init; } = [];

    /// <summary>An optional model ID attributed to versions read from simple documents, which store no ID.</summary>
    /// <remarks>Used for historical schema dispatch when adopting payloads written by other serializers. Never written.</remarks>
    public string? ModelId { get; init; }
}
