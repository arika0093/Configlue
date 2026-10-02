namespace SparseFragments;

/// <summary>Declares that a member's referenced object is safe to share between model clones.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SparseCloneReferenceSafeAttribute : Attribute;
