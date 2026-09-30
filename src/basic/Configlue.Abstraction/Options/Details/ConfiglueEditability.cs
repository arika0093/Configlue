namespace Configlue;

/// <summary>Whether the effective value of a configuration member can be changed through the normal logical save path.</summary>
public enum ConfiglueEditability
{
    /// <summary>The effective value can currently be changed through the normal logical save path.</summary>
    Editable,

    /// <summary>A write target is configured but does not support writes.</summary>
    ReadOnly,

    /// <summary>A higher-priority contribution shadows the writable target, so writing there cannot change the effective value.</summary>
    Shadowed,

    /// <summary>No write target can be resolved for this member.</summary>
    NoWriteTarget,
}
