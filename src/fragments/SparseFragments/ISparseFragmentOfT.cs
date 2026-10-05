using System.ComponentModel;

namespace SparseFragments;

/// <summary>A generated fragment that can be combined with another contribution.</summary>
/// <typeparam name="TSelf">The generated fragment type.</typeparam>
/// <remarks>Advanced contract: implemented by generated fragments.</remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public interface ISparseFragment<TSelf> : ISparseFragment
    where TSelf : class, ISparseFragment<TSelf>
{
    /// <summary>Whether this fragment has no present members.</summary>
    bool IsEmpty { get; }

    /// <summary>Merges a higher-priority contribution over this fragment.</summary>
    TSelf Merge(TSelf higher);

    /// <summary>Applies a sparse semantic diff to this source-local contribution.</summary>
    TSelf ApplyChanges(TSelf changes);
}
