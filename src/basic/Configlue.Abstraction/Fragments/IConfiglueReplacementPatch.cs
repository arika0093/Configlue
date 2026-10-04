namespace Configlue;

/// <summary>A generated patch that can mark every unspecified member as source-local Unset.</summary>
/// <remarks>Advanced patch vocabulary.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueReplacementPatch : IConfigluePatch
{
    /// <summary>Returns a patch that removes members not explicitly set by the caller.</summary>
    IConfigluePatch WithUnspecifiedMembersUnset();
}
