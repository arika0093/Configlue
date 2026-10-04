using System.Diagnostics;

namespace Configlue;

/// <summary>A resolved leaf value with provenance and editability metadata.</summary>
/// <remarks>Advanced diagnostics vocabulary.</remarks>
/// <typeparam name="T">The member value type.</typeparam>
[DebuggerDisplay("{Value}")]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class ConfigValueDetails<T>
{
    /// <summary>Creates leaf details.</summary>
    public ConfigValueDetails(
        T? value,
        ConfiglueEditability editability,
        ConfigSourceDetails? source,
        IReadOnlyList<ConfigSourceValueDetails<T?>> sources
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        Value = value;
        Editability = editability;
        Source = source;
        Sources = sources;
    }

    /// <summary>The effective resolved value.</summary>
    public T? Value { get; }

    /// <summary>Whether the effective value can currently be changed through the normal logical save path.</summary>
    public bool IsEditable => Editability == ConfiglueEditability.Editable;

    /// <summary>Why the effective value can or cannot be changed through the normal logical save path.</summary>
    public ConfiglueEditability Editability { get; }

    /// <summary>The source determining the effective value, when a single source owns it.</summary>
    public ConfigSourceDetails? Source { get; }

    /// <summary>Per-source value states, ordered from highest to lowest priority.</summary>
    public IReadOnlyList<ConfigSourceValueDetails<T?>> Sources { get; }

    /// <summary>Reads the effective value naturally.</summary>
    public static implicit operator T?(ConfigValueDetails<T> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return details.Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value?.ToString() ?? string.Empty;
}
