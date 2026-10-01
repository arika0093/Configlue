using System.ComponentModel;

namespace SparseFragments;

/// <summary>Collection operations used by generated sparse fragments.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SparseCollectionMerger
{
    /// <summary>Merges set-shaped contributions while preserving a concrete HashSet comparer when available.</summary>
    public static HashSet<T> MergeSet<T>(IEnumerable<T> lower, IEnumerable<T> higher)
    {
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(higher);

        var comparer =
            (lower as HashSet<T>)?.Comparer
            ?? (higher as HashSet<T>)?.Comparer
            ?? EqualityComparer<T>.Default;
        var result = new HashSet<T>(lower, comparer);
        result.UnionWith(higher);
        return result;
    }
}
