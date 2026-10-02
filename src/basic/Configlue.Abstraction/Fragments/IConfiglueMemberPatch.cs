namespace Configlue.CompilerServices;

/// <summary>A generated patch that can copy selected member operations without changing their intent.</summary>
/// <remarks>
/// Select operations by generated member IDs from the corresponding schema version. These IDs are schema-local
/// ordinals and must not be persisted or compared across versions. The returned patch preserves Unchanged, Set,
/// and Unset, including explicitly set null/default values. Custom patches can implement this optional contract
/// when they support member-level routing; <see cref="IConfigluePatch"/> remains sufficient for single-source patches.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueDynamicMemberPatch : global::Configlue.IConfigluePatch
{
    /// <summary>Creates a patch containing only operations for member IDs from the matching schema version.</summary>
    /// <param name="memberIds">Member ordinals defined by the model schema for this patch.</param>
    IConfigluePatch SelectMembers(ReadOnlySpan<int> memberIds);
}
