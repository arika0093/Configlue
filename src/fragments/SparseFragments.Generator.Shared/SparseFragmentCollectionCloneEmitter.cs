using System.Collections.Generic;
using System.Linq;

namespace SparseFragments.Generator.Shared;

/// <summary>Emits collection clone helpers and the portable <c>IReadOnlySet{T}</c> view.</summary>
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
        bool includePriorityQueue,
        bool includeImmutableCollections,
        bool includePortableSetView
    )
    {
        code.AppendLineAt(
            1,
            "private static TSet __CloneSet<T, TSet>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
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
        if (includeImmutableCollections)
            code.AppendLineAt(
                2,
                "if (source is global::System.Collections.Immutable.ImmutableHashSet<T> immutableSet) return __CloneImmutableSet<T, TSet>(immutableSet, context, cloneElement);"
            );
        code.AppendLineAt(2, "global::System.Collections.Generic.ISet<T> clone;");
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.Generic.SortedSet<T> sorted) clone = new global::System.Collections.Generic.SortedSet<T>(sorted.Comparer);"
        );
        code.AppendLineAt(
            2,
            "else clone = new global::System.Collections.Generic.HashSet<T>((source as global::System.Collections.Generic.HashSet<T>)?.Comparer);"
        );
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(2, "foreach (var item in source) clone.Add(cloneElement(item));");
        code.AppendLineAt(2, "return (TSet)(object)clone;");
        code.AppendLineAt(1, "}");
        if (includePortableSetView)
        {
            code.AppendLineAt(
                1,
                "private static TSet __CloneSetView<T, TSet>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
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
                "else if (source is global::System.Collections.Generic.SortedSet<T> sorted) clone = new global::System.Collections.Generic.SortedSet<T>(sorted.Comparer);"
            );
            code.AppendLineAt(
                2,
                "else clone = new global::System.Collections.Generic.HashSet<T>((source as global::System.Collections.Generic.HashSet<T>)?.Comparer);"
            );
            code.AppendLineAt(2, "var view = new __SparseReadOnlySet<T>(clone);");
            code.AppendLineAt(2, "context.Add(source, view);");
            code.AppendLineAt(2, "foreach (var item in source) view.Add(cloneElement(item));");
            code.AppendLineAt(2, "return (TSet)(object)view;");
            code.AppendLineAt(1, "}");
        }
        code.AppendLineAt(
            1,
            "private static TDictionary __CloneDictionary<TKey, TValue, TDictionary>(global::System.Collections.Generic.IEnumerable<global::System.Collections.Generic.KeyValuePair<TKey, TValue>> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<TKey, TKey> cloneKey, global::System.Func<TValue, TValue> cloneValue) where TKey : notnull"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TDictionary)existing;"
        );
        if (includeImmutableCollections)
            code.AppendLineAt(
                2,
                "if (source is global::System.Collections.Immutable.ImmutableDictionary<TKey, TValue> immutableDictionary) return __CloneImmutableDictionary<TKey, TValue, TDictionary>(immutableDictionary, context, cloneKey, cloneValue);"
            );
        code.AppendLineAt(2, "global::System.Collections.Generic.IDictionary<TKey, TValue> clone;");
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
            "else clone = new global::System.Collections.Generic.Dictionary<TKey, TValue>((source as global::System.Collections.Generic.Dictionary<TKey, TValue>)?.Comparer);"
        );
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(
            2,
            "foreach (var pair in source) clone.Add(cloneKey(pair.Key), cloneValue(pair.Value));"
        );
        code.AppendLineAt(2, "return (TDictionary)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneArray<T, TCollection>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
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
            "if (source is global::System.Collections.Generic.HashSet<T> || source is global::System.Collections.Generic.SortedSet<T>) return __CloneSet<T, TCollection>(source, context, cloneElement);"
        );
        if (includeImmutableCollections)
        {
            code.AppendLineAt(
                2,
                "if (source is global::System.Collections.Immutable.ImmutableList<T> immutableList) return __CloneImmutableList<T, TCollection>(immutableList, context, cloneElement);"
            );
            code.AppendLineAt(
                2,
                "if (source is global::System.Collections.Immutable.ImmutableHashSet<T> immutableSet) return __CloneImmutableSet<T, TCollection>(immutableSet, context, cloneElement);"
            );
        }
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.Generic.Queue<T>) return __CloneQueue<T, TCollection>(source, context, cloneElement);"
        );
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.Generic.Stack<T>) return __CloneStack<T, TCollection>(source, context, cloneElement);"
        );
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.Concurrent.ConcurrentQueue<T>) return __CloneConcurrentQueue<T, TCollection>(source, context, cloneElement);"
        );
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.Concurrent.ConcurrentStack<T>) return __CloneConcurrentStack<T, TCollection>(source, context, cloneElement);"
        );
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.Concurrent.BlockingCollection<T> blocking) return __CloneBlockingCollection<T, TCollection>(blocking, context, cloneElement);"
        );
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.Generic.LinkedList<T>) return __CloneLinkedList<T, TCollection>(source, context, cloneElement);"
        );
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.ObjectModel.ObservableCollection<T>) return __CloneObservableCollection<T, TCollection>(source, context, cloneElement);"
        );
        code.AppendLineAt(
            2,
            "if (source is global::System.Collections.ObjectModel.ReadOnlyCollection<T>) return __CloneReadOnlyCollection<T, TCollection>(source, context, cloneElement);"
        );
        code.AppendLineAt(
            2,
            "var values = source as T[] ?? global::System.Linq.Enumerable.ToArray(source);"
        );
        code.AppendLineAt(2, "var clone = new T[values.Length];");
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(
            2,
            "for (var index = 0; index < values.Length; index++) clone[index] = cloneElement(values[index]);"
        );
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneList<T, TCollection>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
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
        code.AppendLineAt(2, "var clone = new global::System.Collections.Generic.List<T>();");
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(2, "foreach (var item in source) clone.Add(cloneElement(item));");
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneQueue<T, TCollection>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
        );
        code.AppendLineAt(2, "var clone = new global::System.Collections.Generic.Queue<T>();");
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(2, "foreach (var item in source) clone.Enqueue(cloneElement(item));");
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneStack<T, TCollection>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
        );
        code.AppendLineAt(2, "var clone = new global::System.Collections.Generic.Stack<T>();");
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(
            2,
            "foreach (var item in global::System.Linq.Enumerable.Reverse(source)) clone.Push(cloneElement(item));"
        );
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneConcurrentQueue<T, TCollection>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
        );
        code.AppendLineAt(
            2,
            "var clone = new global::System.Collections.Concurrent.ConcurrentQueue<T>();"
        );
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(2, "foreach (var item in source) clone.Enqueue(cloneElement(item));");
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneConcurrentStack<T, TCollection>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
        );
        code.AppendLineAt(
            2,
            "var clone = new global::System.Collections.Concurrent.ConcurrentStack<T>();"
        );
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(
            2,
            "foreach (var item in global::System.Linq.Enumerable.Reverse(source)) clone.Push(cloneElement(item));"
        );
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneLinkedList<T, TCollection>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
        );
        code.AppendLineAt(2, "var clone = new global::System.Collections.Generic.LinkedList<T>();");
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(2, "foreach (var item in source) clone.AddLast(cloneElement(item));");
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneObservableCollection<T, TCollection>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
        );
        code.AppendLineAt(
            2,
            "var clone = new global::System.Collections.ObjectModel.ObservableCollection<T>();"
        );
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(2, "foreach (var item in source) clone.Add(cloneElement(item));");
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneReadOnlyCollection<T, TCollection>(global::System.Collections.Generic.IEnumerable<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
        );
        code.AppendLineAt(2, "var items = new global::System.Collections.Generic.List<T>();");
        code.AppendLineAt(
            2,
            "var clone = new global::System.Collections.ObjectModel.ReadOnlyCollection<T>(items);"
        );
        code.AppendLineAt(2, "context.Add(source, clone);");
        code.AppendLineAt(2, "foreach (var item in source) items.Add(cloneElement(item));");
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TCollection __CloneBlockingCollection<T, TCollection>(global::System.Collections.Concurrent.BlockingCollection<T> original, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(original, out var existing)) return (TCollection)existing;"
        );
        code.AppendLineAt(
            2,
            "var queue = new global::System.Collections.Concurrent.ConcurrentQueue<T>();"
        );
        code.AppendLineAt(2, "var clone = original.BoundedCapacity >= 0");
        code.AppendLineAt(
            3,
            "? new global::System.Collections.Concurrent.BlockingCollection<T>(queue, original.BoundedCapacity)"
        );
        code.AppendLineAt(
            3,
            ": new global::System.Collections.Concurrent.BlockingCollection<T>(queue);"
        );
        code.AppendLineAt(2, "context.Add(original, clone);");
        code.AppendLineAt(2, "foreach (var item in original) clone.Add(cloneElement(item));");
        code.AppendLineAt(2, "if (original.IsAddingCompleted)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "clone.CompleteAdding();");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "return (TCollection)(object)clone;");
        code.AppendLineAt(1, "}");
        code.AppendLineAt(
            1,
            "private static TClone __CloneImmutableReference<TSource, TClone>(TSource source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<TClone> createClone) where TSource : class"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var existing)) return (TClone)existing;"
        );
        code.AppendLineAt(2, "var clone = createClone();");
        code.AppendLineAt(
            2,
            "if (context.TryGetValue(source, out var completedClone)) return (TClone)completedClone;"
        );
        code.AppendLineAt(2, "context.Add(source, clone!);");
        code.AppendLineAt(2, "return clone;");
        code.AppendLineAt(1, "}");
        if (includePriorityQueue)
        {
            code.AppendLineAt(
                1,
                "private static global::System.Collections.Generic.PriorityQueue<TElement, TPriority> __ClonePriorityQueue<TElement, TPriority>(global::System.Collections.Generic.PriorityQueue<TElement, TPriority> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<TElement, TElement> cloneElement, global::System.Func<TPriority, TPriority> clonePriority)"
            );
            code.AppendLineAt(1, "{");
            code.AppendLineAt(
                2,
                "if (context.TryGetValue(source, out var existing)) return (global::System.Collections.Generic.PriorityQueue<TElement, TPriority>)existing;"
            );
            code.AppendLineAt(
                2,
                "var clone = new global::System.Collections.Generic.PriorityQueue<TElement, TPriority>(source.Comparer);"
            );
            code.AppendLineAt(2, "context.Add(source, clone);");
            code.AppendLineAt(
                2,
                "foreach (var item in source.UnorderedItems) clone.Enqueue(cloneElement(item.Element), clonePriority(item.Priority));"
            );
            code.AppendLineAt(2, "return clone;");
            code.AppendLineAt(1, "}");
        }
        if (includeImmutableCollections)
        {
            code.AppendLineAt(
                1,
                "private static TCollection __CloneImmutableList<T, TCollection>(global::System.Collections.Immutable.ImmutableList<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
            );
            code.AppendLineAt(1, "{");
            code.AppendLineAt(
                2,
                "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
            );
            code.AppendLineAt(
                2,
                "var clone = __CloneImmutableReference(source, context, () => global::System.Collections.Immutable.ImmutableList.CreateRange(global::System.Linq.Enumerable.Select(source, cloneElement)));"
            );
            code.AppendLineAt(2, "return (TCollection)(object)clone;");
            code.AppendLineAt(1, "}");
            code.AppendLineAt(
                1,
                "private static TCollection __CloneImmutableSet<T, TCollection>(global::System.Collections.Immutable.ImmutableHashSet<T> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<T, T> cloneElement)"
            );
            code.AppendLineAt(1, "{");
            code.AppendLineAt(
                2,
                "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
            );
            code.AppendLineAt(
                2,
                "var clone = __CloneImmutableReference(source, context, () => global::System.Collections.Immutable.ImmutableHashSet.CreateRange(source.KeyComparer, global::System.Linq.Enumerable.Select(source, cloneElement)));"
            );
            code.AppendLineAt(2, "return (TCollection)(object)clone;");
            code.AppendLineAt(1, "}");
            code.AppendLineAt(
                1,
                "private static TCollection __CloneImmutableDictionary<TKey, TValue, TCollection>(global::System.Collections.Immutable.ImmutableDictionary<TKey, TValue> source, global::System.Collections.Generic.Dictionary<object, object> context, global::System.Func<TKey, TKey> cloneKey, global::System.Func<TValue, TValue> cloneValue) where TKey : notnull"
            );
            code.AppendLineAt(1, "{");
            code.AppendLineAt(
                2,
                "if (context.TryGetValue(source, out var existing)) return (TCollection)existing;"
            );
            code.AppendLineAt(
                2,
                "var clone = __CloneImmutableReference(source, context, () => global::System.Collections.Immutable.ImmutableDictionary.Create<TKey, TValue>(source.KeyComparer).WithComparers(source.KeyComparer, source.ValueComparer).AddRange(global::System.Linq.Enumerable.Select(source, pair => new global::System.Collections.Generic.KeyValuePair<TKey, TValue>(cloneKey(pair.Key), cloneValue(pair.Value)))));"
            );
            code.AppendLineAt(2, "return (TCollection)(object)clone;");
            code.AppendLineAt(1, "}");
        }
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
                "if (__inner is global::System.Collections.Generic.SortedSet<T> sorted) return new global::System.Collections.Generic.SortedSet<T>(sorted.Comparer);"
            );
            code.AppendLineAt(
                3,
                "if (__inner is global::System.Collections.Generic.HashSet<T> hash) return new global::System.Collections.Generic.HashSet<T>(hash.Comparer);"
            );
            code.AppendLineAt(3, "return new global::System.Collections.Generic.HashSet<T>();");
            code.AppendLineAt(2, "}");
            code.AppendLineAt(1, "}");
        }
    }
}
