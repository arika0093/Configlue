using System.Collections.Generic;
using System.Linq;

namespace SparseFragments.Generator.Shared;

/// <summary>Emits collection clone helpers and the portable <c>IReadOnlySet{T}</c> view.</summary>
/// <remarks>
/// Narrowed (issue #280): only the first-class configuration-model shapes
/// (arrays, lists, sets, dictionaries) get generated clone helpers. Exotic
/// containers (queues, stacks, concurrent collections, blocking collections,
/// priority queues, linked lists, sorted/observable/read-only wrappers and
/// immutable collections) are unsupported and require a custom clone policy.
/// </remarks>
internal static class SparseFragmentCollectionCloneEmitter
{
    public static bool RequiresPortableSetView(
        bool bclHashSetImplementsReadOnlySet,
        IEnumerable<string?> namedTypeDefinitions
    ) =>
        !bclHashSetImplementsReadOnlySet
        && namedTypeDefinitions.Any(definition =>
            definition == SparseWellKnownNames.ReadOnlySetTypeDefinition
        );

    public static void AppendCollectionCloneHelpers(
        SharedIndentedBuilder code,
        bool includePortableSetView,
        bool hashSetSupportsCapacity
    )
    {
        var setCapacity = hashSetSupportsCapacity ? "__CloneCollectionCount(source), " : "";
        code.AppendLineAt(
            1,
            "private static int __CloneCollectionCount<T>(global::System.Collections.Generic.IEnumerable<T> source) => (source as global::System.Collections.Generic.ICollection<T>)?.Count ?? (source as global::System.Collections.Generic.IReadOnlyCollection<T>)?.Count ?? 0;"
        );
        code.AppendLineAt(
            1,
            "private static TSet __CloneSet<T, TSet>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T>? cloneElement)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(2, "if (context.TryGetValue(source, out var existing))");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "if (existing is TSet typed) return typed;");
        if (includePortableSetView)
            code.AppendLineAt(
                3,
                "if (existing is __SparseReadOnlySet<T> view && view.Inner is TSet inner) return inner;"
            );
        code.AppendLineAt(3, "return (TSet)existing;");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "global::System.Collections.Generic.ISet<T> clone;");
        code.AppendLineAt(
            2,
            "clone = new global::System.Collections.Generic.HashSet<T>("
                + setCapacity
                + "(source as global::System.Collections.Generic.HashSet<T>)?.Comparer);"
        );
        code.AppendLineAt(2, "context.Add(source, clone);");
        // A HashSet copy constructor can retain a source's oversized capacity and
        // cross the LOH threshold. Union into the count-sized set instead.
        code.AppendLineAt(2, "if (cloneElement is null) clone.UnionWith(source);");
        code.AppendLineAt(2, "else");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "foreach (var item in source) clone.Add(cloneElement(item));");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "return (TSet)(object)clone;");
        code.AppendLineAt(1, "}");
        if (includePortableSetView)
        {
            code.AppendLineAt(
                1,
                "private static TSet __CloneSetView<T, TSet>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T>? cloneElement)"
            );
            code.AppendLineAt(1, "{");
            code.AppendLineAt(2, "if (context.TryGetValue(source, out var existing))");
            code.AppendLineAt(2, "{");
            code.AppendLineAt(3, "if (existing is TSet typed) return typed;");
            code.AppendLineAt(
                3,
                "if (existing is global::System.Collections.Generic.ISet<T> existingSet)"
            );
            code.AppendLineAt(3, "{");
            code.AppendLineAt(4, "var bridged = new __SparseReadOnlySet<T>(existingSet);");
            code.AppendLineAt(4, "context[source] = bridged;");
            code.AppendLineAt(4, "return (TSet)(object)bridged;");
            code.AppendLineAt(3, "}");
            code.AppendLineAt(3, "return (TSet)existing;");
            code.AppendLineAt(2, "}");
            code.AppendLineAt(2, "global::System.Collections.Generic.ISet<T> clone;");
            code.AppendLineAt(
                2,
                "if (source is __SparseReadOnlySet<T> sourceView) clone = sourceView.CloneEmpty();"
            );
            code.AppendLineAt(
                2,
                "else clone = new global::System.Collections.Generic.HashSet<T>("
                    + setCapacity
                    + "(source as global::System.Collections.Generic.HashSet<T>)?.Comparer);"
            );
            code.AppendLineAt(2, "var view = new __SparseReadOnlySet<T>(clone);");
            code.AppendLineAt(2, "context.Add(source, view);");
            code.AppendLineAt(2, "if (cloneElement is null) clone.UnionWith(source);");
            code.AppendLineAt(2, "else");
            code.AppendLineAt(2, "{");
            code.AppendLineAt(3, "foreach (var item in source) view.Add(cloneElement(item));");
            code.AppendLineAt(2, "}");
            code.AppendLineAt(2, "return (TSet)(object)view;");
            code.AppendLineAt(1, "}");
        }
        code.AppendLineAt(
            1,
            "private static TDictionary __CloneDictionary<TKey, TValue, TDictionary>(global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<TKey, TValue>> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<TKey, TKey>? cloneKey, global::System.Func<TValue, TValue>? cloneValue) where TKey : notnull"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TDictionary)existing;"
        );
        code.AppendLineAt(2, "global::System.Collections.Generic.IDictionary<TKey, TValue> clone;");
        code.AppendLineAt(
            2,
            "if (cloneKey is null && cloneValue is null && source is global::System.Collections.Generic.Dictionary<TKey, TValue> dictionary)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "clone = new global::System.Collections.Generic.Dictionary<TKey, TValue>(dictionary, dictionary.Comparer);"
        );
        code.AppendLineAt(3, "context.Add(source, clone);");
        code.AppendLineAt(3, "return (TDictionary)(object)clone;");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.Generic.SortedDictionary<TKey, TValue> sorted) clone = new global::System.Collections.Generic.SortedDictionary<TKey, TValue>(sorted.Comparer);"
        );
        code.AppendLineAt(
            2,
            "else if (source is global::System.Collections.Generic.SortedList<TKey, TValue> sortedList) clone = new global::System.Collections.Generic.SortedList<TKey, TValue>(sortedList.Comparer);"
        );
        code.AppendLineAt(
            2,
            "else clone = new global::System.Collections.Generic.Dictionary<TKey, TValue>(__CloneCollectionCount(source), (source as global::System.Collections.Generic.Dictionary<TKey, TValue>)?.Comparer);"
        );
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(
            2,
            "foreach (var pair in source) clone.Add(cloneKey is null ? pair.Key : cloneKey(pair.Key), cloneValue is null ? pair.Value : cloneValue(pair.Value));"
        );
        code.AppendLineAt(2, "return (TDictionary)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneArray<T, TCollection>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T>? cloneElement)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
        );
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.Generic.List<T>) return __CloneList<T, TCollection>(source, context, cloneElement);"
        );
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.Generic.HashSet<T>) return __CloneSet<T, TCollection>(source, context, cloneElement);"
        );
        code.AppendLineAt(
            2,
            "var values = source as T[] ?? global::System.Linq.Enumerable.ToArray(source);"
        );
        code.AppendLineAt(2, "var clone = new T[values.Length];");
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(
            2,
            "if (cloneElement is null) global::System.Array.Copy(values, clone, values.Length);"
        );
        code.AppendLineAt(2, "else");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "for (var index = 0; index < values.Length; index++) clone[index] = cloneElement(values[index]);"
        );
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneList<T, TCollection>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T>? cloneElement)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
        );
        code.AppendLineAt(
            2,
            "if (source is T[]) return __CloneArray<T, TCollection>(source, context, cloneElement);"
        );
        code.AppendLineAt(
            2,
            "var clone = new global::System.Collections.Generic.List<T>(__CloneCollectionCount(source));"
        );
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(2, "if (cloneElement is null) clone.AddRange(source);");
        code.AppendLineAt(2, "else");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "foreach (var item in source) clone.Add(cloneElement(item));");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        if (includePortableSetView)
        {
            code.AppendLineAt(
                1,
                "private sealed class __SparseReadOnlySet<T> : global::System.Collections.Generic.ISet<T>, global::System.Collections.Generic.IReadOnlySet<T>"
            );
            code.AppendLineAt(1, "{");
            code.AppendLineAt(
                2,
                "private readonly global::System.Collections.Generic.ISet<T> __inner;"
            );
            code.AppendLineAt(
                2,
                "public __SparseReadOnlySet(global::System.Collections.Generic.ISet<T> inner) => __inner = inner;"
            );
            code.AppendLineAt(
                2,
                "public global::System.Collections.Generic.ISet<T> Inner => __inner;"
            );
            code.AppendLineAt(2, "public int Count => __inner.Count;");
            code.AppendLineAt(2, "public bool IsReadOnly => __inner.IsReadOnly;");
            code.AppendLineAt(2, "public bool Add(T item) => __inner.Add(item);");
            code.AppendLineAt(
                2,
                "void global::System.Collections.Generic.ICollection<T>.Add(T item) => __inner.Add(item);"
            );
            code.AppendLineAt(2, "public void Clear() => __inner.Clear();");
            code.AppendLineAt(2, "public bool Contains(T item) => __inner.Contains(item);");
            code.AppendLineAt(
                2,
                "public void CopyTo(T[] array, int arrayIndex) => __inner.CopyTo(array, arrayIndex);"
            );
            code.AppendLineAt(2, "public bool Remove(T item) => __inner.Remove(item);");
            code.AppendLineAt(
                2,
                "public void ExceptWith(global::System.Collections.Generic.IEnumerable<T> other) => __inner.ExceptWith(other);"
            );
            code.AppendLineAt(
                2,
                "public void IntersectWith(global::System.Collections.Generic.IEnumerable<T> other) => __inner.IntersectWith(other);"
            );
            code.AppendLineAt(
                2,
                "public bool IsProperSubsetOf(global::System.Collections.Generic.IEnumerable<T> other) => __inner.IsProperSubsetOf(other);"
            );
            code.AppendLineAt(
                2,
                "public bool IsProperSupersetOf(global::System.Collections.Generic.IEnumerable<T> other) => __inner.IsProperSupersetOf(other);"
            );
            code.AppendLineAt(
                2,
                "public bool IsSubsetOf(global::System.Collections.Generic.IEnumerable<T> other) => __inner.IsSubsetOf(other);"
            );
            code.AppendLineAt(
                2,
                "public bool IsSupersetOf(global::System.Collections.Generic.IEnumerable<T> other) => __inner.IsSupersetOf(other);"
            );
            code.AppendLineAt(
                2,
                "public bool Overlaps(global::System.Collections.Generic.IEnumerable<T> other) => __inner.Overlaps(other);"
            );
            code.AppendLineAt(
                2,
                "public bool SetEquals(global::System.Collections.Generic.IEnumerable<T> other) => __inner.SetEquals(other);"
            );
            code.AppendLineAt(
                2,
                "public void SymmetricExceptWith(global::System.Collections.Generic.IEnumerable<T> other) => __inner.SymmetricExceptWith(other);"
            );
            code.AppendLineAt(
                2,
                "public void UnionWith(global::System.Collections.Generic.IEnumerable<T> other) => __inner.UnionWith(other);"
            );
            code.AppendLineAt(
                2,
                "public global::System.Collections.Generic.IEnumerator<T> GetEnumerator() => __inner.GetEnumerator();"
            );
            code.AppendLineAt(
                2,
                "global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => __inner.GetEnumerator();"
            );
            code.AppendLineAt(2, "public global::System.Collections.Generic.ISet<T> CloneEmpty()");
            code.AppendLineAt(2, "{");
            code.AppendLineAt(
                3,
                "if (__inner is global::System.Collections.Generic.HashSet<T> hash) return new global::System.Collections.Generic.HashSet<T>("
                    + (hashSetSupportsCapacity ? "hash.Count, " : "")
                    + "hash.Comparer);"
            );
            code.AppendLineAt(
                3,
                "return new global::System.Collections.Generic.HashSet<T>("
                    + (hashSetSupportsCapacity ? "Count, null" : "")
                    + ");"
            );
            code.AppendLineAt(2, "}");
            code.AppendLineAt(1, "}");
        }
    }
}
