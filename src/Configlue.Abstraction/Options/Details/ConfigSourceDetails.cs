namespace Configlue;

/// <summary>How a source is identified and displayed in configuration details.</summary>
public sealed class ConfigSourceDetails
{
    /// <summary>Creates source display metadata.</summary>
    public ConfigSourceDetails(
        string key,
        string kind,
        string displayName,
        string? locator,
        bool canWrite,
        bool canWatch
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
    }

    /// <summary>An opaque stable key for programmatic matching. Not for display.</summary>
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

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
