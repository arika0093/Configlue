namespace Configlue;

/// <summary>A source's sparse contribution to one logical model member.</summary>
public readonly record struct ConfiglueSourceContribution(
    string SourceId,
    string? PhysicalOrigin,
    string? Revision,
    object? Value);

/// <summary>Explains the resolved value and the source contributions for one model path.</summary>
public sealed class ConfiglueValueExplanation
{
    /// <summary>Creates an immutable explanation.</summary>
    public ConfiglueValueExplanation(
        string propertyPath,
        object? effectiveValue,
        IEnumerable<ConfiglueSourceContribution> contributions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        ArgumentNullException.ThrowIfNull(contributions);
        PropertyPath = propertyPath;
        EffectiveValue = effectiveValue;
        Contributions = Array.AsReadOnly(contributions.ToArray());
    }

    /// <summary>The CLR property path, with nested members separated by periods.</summary>
    public string PropertyPath { get; }

    /// <summary>The value on the resolved model. Nested model members are returned as model instances.</summary>
    public object? EffectiveValue { get; }

    /// <summary>Present source contributions, ordered from highest to lowest priority.</summary>
    public IReadOnlyList<ConfiglueSourceContribution> Contributions { get; }

    /// <summary>The highest-priority source that contributes this member, if any.</summary>
    public string? HighestPrioritySourceId => Contributions.Count == 0 ? null : Contributions[0].SourceId;
}
