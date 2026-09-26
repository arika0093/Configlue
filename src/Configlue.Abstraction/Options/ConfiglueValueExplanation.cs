namespace Configlue;

/// <summary>A source's sparse contribution to one logical model member.</summary>
public readonly record struct ConfiglueSourceContribution(
    string SourceId,
    string? PhysicalOrigin,
    string? Revision,
    object? Value
);

/// <summary>Explains which sources contributed one effective collection element.</summary>
public sealed class ConfiglueCollectionElementExplanation
{
    /// <summary>Creates an immutable element explanation.</summary>
    public ConfiglueCollectionElementExplanation(
        int index,
        object? value,
        IEnumerable<ConfiglueSourceContribution> contributions
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(contributions);
        Index = index;
        Value = value;
        Contributions = Array.AsReadOnly(contributions.ToArray());
    }

    /// <summary>The element's index in the effective collection.</summary>
    public int Index { get; }

    /// <summary>The element value in the effective collection.</summary>
    public object? Value { get; }

    /// <summary>
    /// Source contributions to this element, ordered from highest to lowest priority. Each contribution's
    /// <see cref="ConfiglueSourceContribution.Value"/> is the element value.
    /// </summary>
    public IReadOnlyList<ConfiglueSourceContribution> Contributions { get; }
}

/// <summary>Explains the resolved value and the source contributions for one model path.</summary>
public sealed class ConfiglueValueExplanation
{
    /// <summary>Creates an immutable explanation.</summary>
    public ConfiglueValueExplanation(
        string propertyPath,
        object? effectiveValue,
        IEnumerable<ConfiglueSourceContribution> contributions
    )
        : this(propertyPath, effectiveValue, contributions, []) { }

    /// <summary>Creates an immutable explanation with optional per-element provenance.</summary>
    public ConfiglueValueExplanation(
        string propertyPath,
        object? effectiveValue,
        IEnumerable<ConfiglueSourceContribution> contributions,
        IEnumerable<ConfiglueCollectionElementExplanation> collectionElements
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        ArgumentNullException.ThrowIfNull(contributions);
        ArgumentNullException.ThrowIfNull(collectionElements);
        PropertyPath = propertyPath;
        EffectiveValue = effectiveValue;
        Contributions = Array.AsReadOnly(contributions.ToArray());
        CollectionElements = Array.AsReadOnly(collectionElements.ToArray());
    }

    /// <summary>The CLR property path, with nested members separated by periods.</summary>
    public string PropertyPath { get; }

    /// <summary>The value on the resolved model. Nested model members are returned as model instances.</summary>
    public object? EffectiveValue { get; }

    /// <summary>Present source contributions, ordered from highest to lowest priority.</summary>
    public IReadOnlyList<ConfiglueSourceContribution> Contributions { get; }

    /// <summary>
    /// Per-element provenance when this path names a generated collection member. Elements follow the
    /// enumeration order of <see cref="EffectiveValue"/>; this list is empty for non-collection members.
    /// </summary>
    public IReadOnlyList<ConfiglueCollectionElementExplanation> CollectionElements { get; }

    /// <summary>The highest-priority source that contributes this member, if any.</summary>
    public string? HighestPrioritySourceId =>
        Contributions.Count == 0 ? null : Contributions[0].SourceId;
}
