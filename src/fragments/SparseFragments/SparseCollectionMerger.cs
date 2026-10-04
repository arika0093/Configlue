using System.ComponentModel;

#if CONFIGLUE_FRAGMENT_RUNTIME
namespace Configlue;

#else
namespace SparseFragments;

#endif

/// <summary>Collection operations used by generated sparse fragments.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
#if CONFIGLUE_FRAGMENT_RUNTIME
public static class ConfiglueCollectionMerger
#else
public static class SparseCollectionMerger
#endif
{
    /// <summary>Merges ordered contributions, retaining the first occurrence of each value.</summary>
    public static T[] MergeDistinctArray<T>(IEnumerable<T> lower, IEnumerable<T> higher) =>
        MergeDistinctList(lower, higher).ToArray();

    /// <summary>Merges ordered contributions, retaining the first occurrence of each value.</summary>
    public static List<T> MergeDistinctList<T>(IEnumerable<T> lower, IEnumerable<T> higher)
    {
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(higher);
        var lowerCount = GetCount(lower);
        var higherCount = GetCount(higher);
        var capacity = checked(lowerCount + higherCount);
#if NETSTANDARD2_0
        var seen = new HashSet<T>();
#else
        var seen = new HashSet<T>(capacity);
#endif
        var result = new List<T>(capacity);
        AddDistinct(lower, seen, result);
        AddDistinct(higher, seen, result);
        return result;
    }

    private static int GetCount<T>(IEnumerable<T> values) =>
        values is ICollection<T> collection ? collection.Count : 0;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S3267",
        Justification = "Typed array/List loops avoid iterator and enumerator allocations during collection merge."
    )]
    private static void AddDistinct<T>(IEnumerable<T> values, HashSet<T> seen, List<T> result)
    {
        if (values is T[] array)
        {
            foreach (var value in array)
                if (seen.Add(value))
                    result.Add(value);
        }
        else if (values is List<T> list)
        {
            foreach (var value in list)
                if (seen.Add(value))
                    result.Add(value);
        }
        else
        {
            foreach (var value in values)
                if (seen.Add(value))
                    result.Add(value);
        }
    }

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
