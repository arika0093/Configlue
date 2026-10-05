using System.ComponentModel;

namespace SparseFragments;

/// <summary>Implemented by source-generated models that support deep cloning.</summary>
/// <remarks>Advanced contract: implemented by generated models.</remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public interface ISparseDeepCloneable<out T>
{
    /// <summary>
    /// Creates an independent deep clone, preserving graph aliases and cycles.
    /// Members marked with <see cref="SparseCloneReferenceSafeAttribute"/> retain their original references.
    /// </summary>
    T DeepClone();
}
