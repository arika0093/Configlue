namespace Configlue;

/// <summary>How a source is identified and displayed in configuration details.</summary>
/// <remarks>Advanced diagnostics vocabulary.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class ConfigSourceDetails
{
    /// <summary>Creates source display metadata.</summary>
    public ConfigSourceDetails(
        string key,
        string kind,
        string displayName,
        string? locator,
        bool canWrite,
        bool canWatch,
        ConfigSourceResolutionDetails? resolution = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Key = key;
        Kind = kind;
        DisplayName = displayName;
        Locator = locator;
        CanWrite = canWrite;
        CanWatch = canWatch;
        Resolution = resolution;
    }

    /// <summary>An opaque stable key for matching this source across snapshots from one state instance. Not a source ID or display value.</summary>
    public string Key { get; }

    /// <summary>The source kind, such as <c>Environment</c>, <c>File</c>, <c>Http</c>, or <c>CommandLine</c>.</summary>
    public string Kind { get; }

    /// <summary>A human-readable source label.</summary>
    public string DisplayName { get; }

    /// <summary>A generic locator for the source, such as a file path, endpoint, or variable prefix.</summary>
    public string? Locator { get; }

    /// <summary>Whether the source supports writes.</summary>
    public bool CanWrite { get; }

    /// <summary>Whether the source supports change notifications.</summary>
    public bool CanWatch { get; }

    /// <summary>
    /// The subject routing and physical placement used by the read that produced this snapshot.
    /// Null for model defaults and for sources without resolvable placement; server-wide reads
    /// without a subject use <see cref="SubjectKey.Default"/> and <see cref="RouteKey.Default"/>.
    /// </summary>
    public ConfigSourceResolutionDetails? Resolution { get; }

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
