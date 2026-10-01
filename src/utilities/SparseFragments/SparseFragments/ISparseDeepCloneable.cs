namespace SparseFragments;

/// <summary>Implemented by source-generated models that support deep cloning.</summary>
public interface ISparseDeepCloneable<out T>
{
    /// <summary>Creates an independent deep clone.</summary>
    T DeepClone();
}
