using System.Diagnostics;

namespace Configlue;

/// <summary>One effective collection element with its source provenance.</summary>
/// <remarks>Advanced diagnostics vocabulary.</remarks>
/// <typeparam name="T">The element type.</typeparam>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class ConfigCollectionElementDetails<T>
{
    /// <summary>Creates element details.</summary>
    public ConfigCollectionElementDetails(
        int index,
        T? value,
        IReadOnlyList<ConfigSourceValueDetails<T?>> contributions,
        bool isSecret = false
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(contributions);
        Index = index;
        Value = value;
        Contributions = contributions;
        IsSecret = isSecret;
    }

    /// <summary>The element index in the effective collection.</summary>
    public int Index { get; }

    /// <summary>The element value in the effective collection.</summary>
    public T? Value { get; }

    /// <summary>Source contributions to this element, ordered from highest to lowest priority.</summary>
    public IReadOnlyList<ConfigSourceValueDetails<T?>> Contributions { get; }

    /// <summary>
    /// Whether this element belongs to a sensitive collection subtree.
    /// Generic display surfaces redact the value when true; typed <see cref="Value"/>
    /// access still returns the real value.
    /// </summary>
    public bool IsSecret { get; }

    private string DebuggerDisplay => ToString();

    /// <inheritdoc />
    public override string ToString() => ConfiglueSecrets.FormatValue(Value, IsSecret);
}

/// <summary>A resolved collection value with provenance and per-element details.</summary>
/// <remarks>Advanced diagnostics vocabulary.</remarks>
/// <typeparam name="T">The element type.</typeparam>
[DebuggerDisplay("Count = {Value.Count}")]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class ConfigCollectionDetails<T>
{
    /// <summary>Creates collection details.</summary>
    public ConfigCollectionDetails(
        IReadOnlyList<T> value,
        ConfiglueEditability editability,
        ConfigSourceDetails? source,
        IReadOnlyList<ConfigSourceValueDetails<IReadOnlyList<T>?>> sources,
        IReadOnlyList<ConfigCollectionElementDetails<T>> elements,
        bool isSecret = false
    )
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(elements);
        Value = value;
        Editability = editability;
        Source = source;
        Sources = sources;
        Elements = elements;
        IsSecret = isSecret;
    }

    /// <summary>The effective resolved collection.</summary>
    public IReadOnlyList<T> Value { get; }

    /// <summary>Whether the effective value can currently be changed through the normal logical save path.</summary>
    public bool IsEditable => Editability == ConfiglueEditability.Editable;

    /// <summary>Why the effective value can or cannot be changed through the normal logical save path.</summary>
    public ConfiglueEditability Editability { get; }

    /// <summary>The source determining the effective value, when a single source owns it.</summary>
    public ConfigSourceDetails? Source { get; }

    /// <summary>Per-source value states, ordered from highest to lowest priority.</summary>
    public IReadOnlyList<ConfigSourceValueDetails<IReadOnlyList<T>?>> Sources { get; }

    /// <summary>Per-element provenance following the effective enumeration order.</summary>
    public IReadOnlyList<ConfigCollectionElementDetails<T>> Elements { get; }

    /// <summary>
    /// Whether this collection belongs to a sensitive member subtree.
    /// Element values are treated as sensitive when true; typed <see cref="Value"/>
    /// access still returns the real values. Collection counts remain visible.
    /// </summary>
    public bool IsSecret { get; }

    /// <inheritdoc />
    public override string ToString() => $"Count = {Value.Count}";
}
