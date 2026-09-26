namespace Configlue;

/// <summary>Configures fragment merge behavior for a model member.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class ConfiglueMergeAttribute(MergeMode mode) : Attribute
{
    /// <summary>The merge operation used by generated fragment resolution.</summary>
    public MergeMode Mode { get; } = mode;
}
