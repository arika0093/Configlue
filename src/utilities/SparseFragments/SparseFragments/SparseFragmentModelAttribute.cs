namespace SparseFragments;

/// <summary>Marks a partial class or struct for sparse-fragment generation without Configlue.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class SparseFragmentModelAttribute : Attribute
{
    /// <summary>Initializes a new instance of the attribute.</summary>
    public SparseFragmentModelAttribute() { }
}
