namespace SparseFragments;

/// <summary>
/// Declares that a member's referenced object is safe to share between model clones.
/// The generated clone preserves the original reference for this member.
/// Apply this only when the referenced value is immutable or sharing it is otherwise safe.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SparseCloneReferenceSafeAttribute : Attribute;
