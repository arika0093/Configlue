namespace Configlue;

/// <summary>Configures fragment merge behavior for a model member.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class ConfiglueMergeAttribute : Attribute
{
    /// <summary>Selects a built-in merge operation.</summary>
    public ConfiglueMergeAttribute(MergeMode mode)
    {
        Mode = mode;
    }

    /// <summary>Uses a strategy type implementing <see cref="ConfiglueMergeStrategy{T}"/> for the member type.</summary>
    public ConfiglueMergeAttribute(Type strategyType)
    {
        ArgumentNullException.ThrowIfNull(strategyType);
        Mode = MergeMode.Custom;
        StrategyType = strategyType;
    }

    /// <summary>The merge operation used by generated fragment resolution.</summary>
    public MergeMode Mode { get; }

    /// <summary>The custom strategy type, or null when a built-in mode is selected.</summary>
    public Type? StrategyType { get; }
}
