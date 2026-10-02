using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace SparseFragments.Generator.Shared;

/// <summary>Product-neutral fragment algebra emission, configured with runtime names.</summary>
internal sealed class SparseFragmentCoreEmitter(
    string optional,
    string mergeStrategyFieldPrefix,
    string cloneContext,
    string referenceComparer,
    SparseFragmentExpressions expressions
)
{
    private string Optional { get; } = optional;
    private string MergeStrategyFieldPrefix { get; } = mergeStrategyFieldPrefix;
    private string CloneContext { get; } = cloneContext;
    private string ReferenceComparer { get; } = referenceComparer;
    private SparseFragmentExpressions Expressions { get; } = expressions;

    public static bool RequiresPortableSetView(
        bool bclHashSetImplementsReadOnlySet,
        IEnumerable<string?> namedTypeDefinitions
    ) =>
        !bclHashSetImplementsReadOnlySet
        && namedTypeDefinitions.Any(definition =>
            definition == SparseWellKnownNames.ReadOnlySetTypeDefinition
        );

    public string MergeStrategyField(SparseMemberModel member) =>
        MergeStrategyFieldPrefix + member.Id;

    private static string FragmentValueType(SparseMemberModel member) =>
        member.ChildModel is null ? member.Property.Type.Name : member.ChildFragmentType + "?";

    public void AppendBuilder(SharedIndentedBuilder code, ImmutableArray<SparseMemberModel> members)
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(1, "/// <summary>A mutable builder for a generated fragment.</summary>");
        code.AppendLineAt(1, "public sealed class FragmentBuilder");
        code.AppendLineAt(1, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var field = "__sparse_builder_member_" + member.Id;
            code.AppendIndent(2)
                .Append("private ")
                .Append(Optional)
                .Append("<")
                .Append(FragmentValueType(member))
                .Append("> ")
                .Append(field)
                .AppendLine(";");
            code.AppendIndent(2)
                .Append("public ref ")
                .Append(Optional)
                .Append("<")
                .Append(FragmentValueType(member))
                .Append("> ")
                .Append(name)
                .Append(" => ref ")
                .Append(field)
                .AppendLine(";");
        }

        code.AppendLineAt(2, "public FragmentBuilder() { }");
        code.AppendLineAt(2, "internal FragmentBuilder(Fragment fragment)");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3).Append(name).Append(" = fragment.").Append(name).AppendLine(";");
        }

        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "public Fragment Build() => new()");
        code.AppendLineAt(2, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            code.AppendIndent(3).Append(name).Append(" = ").Append(name).AppendLine(",");
        }

        code.AppendLineAt(2, "};");
        code.AppendLineAt(1, "}");
    }

    private void AppendCloneContext(SharedIndentedBuilder code, int indent) =>
        code.AppendLineAt(
            indent,
            "var "
                + CloneContext
                + " = new global::System.Collections.Generic.Dictionary<object, object>("
                + ReferenceComparer
                + ".Instance);"
        );

    private static void AppendNullGuard(SharedIndentedBuilder code, int indent, string variable)
    {
        code.AppendLineAt(indent, "if (" + variable + " is null)");
        code.AppendLineAt(indent, "{");
        code.AppendLineAt(
            indent + 1,
            "throw new global::System.ArgumentNullException(nameof(" + variable + "));"
        );
        code.AppendLineAt(indent, "}");
    }

    public static void AppendDeclaration(
        SharedIndentedBuilder code,
        string fragmentInterface,
        string deepCloneable,
        System.Action<SharedIndentedBuilder>? appendAttributes = null
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            1,
            "/// <summary>A sparse, presence-aware representation of this model.</summary>"
        );
        appendAttributes?.Invoke(code);
        code.AppendLineAt(
            1,
            "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]"
        );
        code.AppendLineAt(
            1,
            "public sealed class Fragment : "
                + fragmentInterface
                + "<Fragment>, "
                + deepCloneable
                + "<Fragment>"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(2, "public Fragment() { }");
        code.AppendLine();
    }

    public void AppendMembers(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        string mergeStrategy,
        System.Action<SharedIndentedBuilder>? appendMemberAttributes = null
    )
    {
        foreach (var member in members)
        {
            code.CancellationToken.ThrowIfCancellationRequested();
            appendMemberAttributes?.Invoke(code);
            code.AppendIndent(2)
                .Append("public ")
                .Append(Optional)
                .Append("<")
                .Append(FragmentValueType(member))
                .Append("> ")
                .Append(SparseNaming.EscapeIdentifier(member.Property.Name))
                .AppendLine(" { get; init; }");
        }
        foreach (var member in members.Where(static member => member.MergeStrategyType is not null))
        {
            code.AppendIndent(2)
                .Append("internal static readonly ")
                .Append(mergeStrategy)
                .Append("<")
                .Append(member.Property.Type.Name)
                .Append("> ")
                .Append(MergeStrategyField(member))
                .Append(" = new ")
                .Append(member.MergeStrategyType!.Value.Name)
                .AppendLine("();");
        }
        code.AppendLine();
        code.AppendLineAt(
            2,
            "/// <summary>Whether this fragment has no present members.</summary>"
        );
        code.AppendIndent(2)
            .Append("public bool IsEmpty => ")
            .Append(
                members.Length == 0
                    ? "true"
                    : string.Join(
                        " && ",
                        members.Select(member =>
                            "!" + SparseNaming.EscapeIdentifier(member.Property.Name) + ".IsPresent"
                        )
                    )
            )
            .AppendLine(";");
        code.AppendLine();
    }

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

    public void AppendDeepClone(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool usesPocoCloning,
        ModelConstructorBinding? constructor = null,
        bool modelIsReferenceType = true
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendIndent(1).Append("public ").Append(modelType).AppendLine(" DeepClone()");
        code.AppendLineAt(1, "{");
        AppendCloneContext(code, 2);
        code.AppendLineAt(2, "return DeepClone(" + CloneContext + ");");
        code.AppendLineAt(1, "}");
        code.AppendIndent(1)
            .Append("public ")
            .Append(modelType)
            .Append(" DeepClone(global::System.Collections.Generic.Dictionary<object, object> ")
            .Append(CloneContext)
            .AppendLine(")");
        code.AppendLineAt(1, "{");
        if (modelIsReferenceType)
            code.AppendLineAt(
                2,
                "if ("
                    + CloneContext
                    + ".TryGetValue(this, out var existing)) return ("
                    + modelType
                    + ")existing;"
            );
        if (
            modelIsReferenceType
            && (constructor is null || constructor.Parameters.IsEmpty)
            && ModelConstructionPlan.ForMembers(members).CanOverlayAfterConstruction
        )
        {
            code.AppendLineAt(2, "var clone = new " + modelType + "();");
            code.AppendLineAt(2, CloneContext + ".Add(this, clone);");
            foreach (var member in members.Where(static member => !member.Property.IsReadOnly))
            {
                var name = SparseNaming.EscapeIdentifier(member.Property.Name);
                code.AppendLineAt(
                    2,
                    "clone."
                        + name
                        + " = "
                        + Expressions.CloneModelExpression(member, "this." + name)
                        + ";"
                );
            }
            code.AppendLineAt(2, "return clone;");
            code.AppendLineAt(1, "}");
            return;
        }

        var boundClones = new Dictionary<string, string>(StringComparer.Ordinal);
        if (constructor is not null)
        {
            foreach (
                var propertyName in constructor.Parameters.Select(static parameter =>
                    parameter.PropertyName
                )
            )
            {
                if (boundClones.ContainsKey(propertyName))
                    continue;
                var member = members.Single(candidate => candidate.Property.Name == propertyName);
                var local = "__constructor_clone_" + boundClones.Count;
                boundClones.Add(propertyName, local);
                code.AppendLineAt(
                    2,
                    "var "
                        + local
                        + " = "
                        + Expressions.CloneModelExpression(
                            member,
                            "this." + SparseNaming.EscapeIdentifier(propertyName)
                        )
                        + ";"
                );
            }
        }
        var arguments = constructor is null
            ? string.Empty
            : string.Join(
                ", ",
                constructor.Parameters.Select(parameter => boundClones[parameter.PropertyName])
            );
        code.AppendIndent(2)
            .Append("var clone = new ")
            .Append(modelType)
            .Append("(")
            .Append(arguments)
            .AppendLine(")");
        code.AppendLineAt(1, "{");
        foreach (var member in members.Where(static member => !member.Property.IsReadOnly))
        {
            code.AppendIndent(2)
                .Append(SparseNaming.EscapeIdentifier(member.Property.Name))
                .Append(" = ")
                .Append(
                    boundClones.TryGetValue(member.Property.Name, out var cloned)
                        ? cloned
                        : Expressions.CloneModelExpression(
                            member,
                            "this." + SparseNaming.EscapeIdentifier(member.Property.Name)
                        )
                )
                .AppendLine(",");
        }

        code.AppendLineAt(1, "};");
        if (modelIsReferenceType)
            code.AppendLineAt(2, CloneContext + ".Add(this, clone);");
        code.AppendLineAt(2, "return clone;");
        code.AppendLineAt(1, "}");
    }

    public void AppendPocoCloneHelper(
        SharedIndentedBuilder code,
        string typeName,
        string cloneHelperName,
        ImmutableArray<SparseMemberModel> members,
        ModelConstructorBinding? constructor = null
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendIndent(1)
            .Append("private static ")
            .Append(typeName)
            .Append(' ')
            .Append(cloneHelperName)
            .Append('(')
            .Append(typeName)
            .AppendLine(
                " value, global::System.Collections.Generic.Dictionary<object, object> "
                    + CloneContext
                    + ")"
            );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(
            2,
            "if ("
                + CloneContext
                + ".TryGetValue(value, out var existing)) { return ("
                + typeName
                + ")existing; }"
        );
        var boundClones = new Dictionary<string, string>(StringComparer.Ordinal);
        if (constructor is not null)
        {
            foreach (
                var propertyName in constructor.Parameters.Select(static parameter =>
                    parameter.PropertyName
                )
            )
            {
                if (boundClones.ContainsKey(propertyName))
                    continue;
                var member = members.Single(candidate => candidate.Property.Name == propertyName);
                var local = "__constructor_clone_" + boundClones.Count;
                boundClones.Add(propertyName, local);
                code.AppendLineAt(
                    2,
                    "var "
                        + local
                        + " = "
                        + Expressions.CloneModelExpression(
                            member,
                            "value." + SparseNaming.EscapeIdentifier(propertyName)
                        )
                        + ";"
                );
            }
        }
        var arguments = constructor is null
            ? string.Empty
            : string.Join(
                ", ",
                constructor.Parameters.Select(parameter => boundClones[parameter.PropertyName])
            );
        code.AppendIndent(2)
            .Append("var clone = new ")
            .Append(typeName)
            .Append("(")
            .Append(arguments)
            .AppendLine(");");
        code.AppendLineAt(2, CloneContext + ".Add(value, clone);");
        foreach (
            var member in members.Where(static member =>
                !member.Property.IsReadOnly && !member.Property.IsInitOnly
            )
        )
        {
            var memberName = SparseNaming.EscapeIdentifier(member.Property.Name);
            code.AppendIndent(2)
                .Append("clone.")
                .Append(memberName)
                .Append(" = ")
                .Append(
                    boundClones.TryGetValue(member.Property.Name, out var cloned)
                        ? cloned
                        : Expressions.CloneModelExpression(member, "value." + memberName)
                )
                .AppendLine(";");
        }

        code.AppendLineAt(2, "return clone;");
        code.AppendLineAt(1, "}");
    }

    public void AppendFromModel(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool modelIsReferenceType,
        bool usesPocoCloning
    )
    {
        code.AppendIndent(2)
            .Append("public static Fragment From(")
            .Append(modelType)
            .AppendLine(" value)");
        code.AppendLineAt(2, "{");
        if (modelIsReferenceType)
        {
            AppendNullGuard(code, 3, "value");
        }

        AppendCloneContext(code, 3);

        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var access = "value." + SparseNaming.EscapeIdentifier(member.Property.Name);
            string value;
            if (member.ChildModel is null)
            {
                value = Expressions.CloneModelExpression(member, access);
            }
            else if (!member.ChildIsReferenceType)
            {
                value = $"{member.ChildFragmentType}.From({access})";
            }
            else
            {
                value = $"({access} is null ? null : {member.ChildFragmentType}.From({access}))";
            }

            code.AppendIndent(4)
                .Append(SparseNaming.EscapeIdentifier(member.Property.Name))
                .Append(" = ")
                .Append(Optional)
                .Append("<")
                .Append(FragmentValueType(member))
                .Append(">.Present(")
                .Append(value)
                .AppendLine("),");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    public static void AppendRootProjectionConstructor(
        SharedIndentedBuilder code,
        string modelName,
        ImmutableArray<SparseMemberModel> members,
        ModelConstructorBinding? constructor = null
    )
    {
        if (
            ModelConstructionPlan.ForMembers(members).CanOverlayAfterConstruction
            && (constructor is null || constructor.Parameters.IsEmpty)
        )
            return;
        code.AppendLineAt(1, "private readonly struct __SparseProjectionToken { }");
        if (constructor?.IsImplicitParameterlessClassConstructor == true)
            code.AppendLineAt(1, "public " + modelName + "() { }");
        if (members.Any(static member => member.Property.IsRequired))
            code.AppendLineAt(1, "[global::System.Diagnostics.CodeAnalysis.SetsRequiredMembers]");
        var arguments = constructor is null
            ? string.Empty
            : string.Join(
                ", ",
                constructor.Parameters.Select(parameter =>
                {
                    var member = members.Single(candidate =>
                        candidate.Property.Name == parameter.PropertyName
                    );
                    var access =
                        "__sparse_projection."
                        + SparseNaming.EscapeIdentifier(parameter.PropertyName);
                    var value = access + ".Value!";
                    if (member.ChildModel is not null)
                        value = member.ChildIsReferenceType
                            ? access + ".Value?.ToModel()!"
                            : access + ".Value!.ToModel()";
                    return access + ".IsPresent ? " + value + " : " + parameter.DefaultExpression;
                })
            );
        code.AppendLineAt(
            1,
            "private "
                + modelName
                + "(Fragment __sparse_projection, __SparseProjectionToken _) : this("
                + arguments
                + ")"
        );
        code.AppendLineAt(1, "{");
        foreach (var member in members.Where(static member => !member.Property.IsReadOnly))
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var access = "__sparse_projection." + name;
            var value = access + ".Value!";
            if (member.ChildModel is not null)
                value = member.ChildIsReferenceType
                    ? access + ".Value?.ToModel()!"
                    : access + ".Value!.ToModel()";
            if (member.Property.IsRequired)
                code.AppendLineAt(
                    2,
                    "this."
                        + name
                        + " = "
                        + access
                        + ".IsPresent ? "
                        + value
                        + " : this."
                        + name
                        + "!;"
                );
            else
                code.AppendLineAt(
                    2,
                    "if (" + access + ".IsPresent) this." + name + " = " + value + ";"
                );
        }
        code.AppendLineAt(1, "}");
    }

    public static void AppendToModel(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool hasRootProjectionConstructor = false,
        ModelConstructorBinding? constructor = null
    )
    {
        code.AppendIndent(2)
            .Append("public ")
            .Append(modelType)
            .Append(" ToModel(")
            .Append(modelType)
            .AppendLine(" baseline)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "return From(baseline).Merge(this).ToModel();");
        code.AppendLineAt(2, "}");
        code.AppendLine();
        code.AppendIndent(2).Append("public ").Append(modelType).AppendLine(" ToModel()");
        code.AppendLineAt(2, "{");
        var construction = hasRootProjectionConstructor
            ? ModelConstructionPlan.ForMembers(members)
            : ModelConstructionPlan.ForStructuralMembers(members, constructor);
        if (
            hasRootProjectionConstructor
            && (
                !construction.CanOverlayAfterConstruction
                || (constructor is not null && !constructor.Parameters.IsEmpty)
            )
        )
        {
            code.AppendLineAt(
                3,
                "return new " + modelType + "(this, default(__SparseProjectionToken));"
            );
            code.AppendLineAt(2, "}");
            code.AppendLine();
            return;
        }
        if (construction.CanOverlayAfterConstruction)
        {
            var arguments = constructor is null
                ? string.Empty
                : string.Join(
                    ", ",
                    constructor.Parameters.Select(parameter =>
                    {
                        var member = members.Single(candidate =>
                            candidate.Property.Name == parameter.PropertyName
                        );
                        var name = SparseNaming.EscapeIdentifier(parameter.PropertyName);
                        var projected = name + ".Value!";
                        if (member.ChildModel is not null)
                            projected = member.ChildIsReferenceType
                                ? name + ".Value?.ToModel()!"
                                : name + ".Value!.ToModel()";
                        return name
                            + ".IsPresent ? "
                            + projected
                            + " : "
                            + parameter.DefaultExpression;
                    })
                );
            code.AppendLineAt(3, "var value = new " + modelType + "(" + arguments + ");");
            foreach (
                var member in members.Where(static member =>
                    !member.Property.IsReadOnly && !member.Property.IsInitOnly
                )
            )
            {
                var name = SparseNaming.EscapeIdentifier(member.Property.Name);
                var projected = name + ".Value!";
                if (member.ChildModel is not null)
                    projected = member.ChildIsReferenceType
                        ? name + ".Value?.ToModel()!"
                        : name + ".Value!.ToModel()";
                code.AppendLineAt(
                    3,
                    "if (" + name + ".IsPresent) value." + name + " = " + projected + ";"
                );
            }
            code.AppendLineAt(3, "return value;");
            code.AppendLineAt(2, "}");
            code.AppendLine();
            return;
        }
        if (!members.IsEmpty)
        {
            code.AppendIndent(3).Append("if (");
            for (var index = 0; index < members.Length; index++)
            {
                if (index > 0)
                {
                    code.Append(" && ");
                }

                code.Append(SparseNaming.EscapeIdentifier(members[index].Property.Name))
                    .Append(".IsPresent");
            }

            code.AppendLine(")");
            code.AppendLineAt(3, "{");
            AppendModelInitializer(code, modelType, members, 4, false);
            code.AppendLineAt(3, "}");
        }

        code.AppendIndent(3).Append("var defaults = new ").Append(modelType).AppendLine("();");
        AppendModelInitializer(code, modelType, members, 3, true);
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    private static void AppendModelInitializer(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        int indent,
        bool useDefaults
    )
    {
        code.AppendIndent(indent).Append("return new ").Append(modelType).AppendLine();
        code.AppendLineAt(indent, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            string value;
            if (member.ChildModel is null)
            {
                value = name + ".Value!";
            }
            else if (!member.ChildIsReferenceType)
            {
                value = name + ".Value!.ToModel()";
            }
            else
            {
                value = name + ".Value?.ToModel()!";
            }

            code.AppendIndent(indent + 1)
                .Append(name)
                .Append(" = ")
                .Append(
                    useDefaults ? name + ".IsPresent ? " + value + " : defaults." + name : value
                )
                .AppendLine(",");
        }

        code.AppendLineAt(indent, "};");
    }

    public void AppendMerge(SharedIndentedBuilder code, ImmutableArray<SparseMemberModel> members)
    {
        var hasCustomMergeStrategy = false;
        var replaceOnly = members.Length > 0;
        foreach (var member in members)
        {
            if (member.MergeStrategyType is not null)
            {
                hasCustomMergeStrategy = true;
                replaceOnly = false;
                continue;
            }

            if (
                (member.MergeMode == 1 && member.ChildModel is not null)
                || member.MergeMode is 2 or 3
            )
            {
                replaceOnly = false;
            }
        }

        code.AppendLineAt(
            2,
            "/// <summary>Merges a higher-priority fragment over this fragment.</summary>"
        );
        code.AppendLineAt(2, "public Fragment Merge(Fragment higherPriority)");
        code.AppendLineAt(2, "{");
        AppendNullGuard(code, 3, "higherPriority");
        if (!hasCustomMergeStrategy)
        {
            code.AppendLineAt(3, "if (higherPriority.IsEmpty)");
            code.AppendLineAt(3, "{");
            code.AppendLineAt(4, "return this;");
            code.AppendLineAt(3, "}");
            code.AppendLineAt(3, "if (IsEmpty)");
            code.AppendLineAt(3, "{");
            code.AppendLineAt(4, "return higherPriority;");
            code.AppendLineAt(3, "}");
        }

        if (replaceOnly)
        {
            code.AppendIndent(3).Append("if (");
            for (var index = 0; index < members.Length; index++)
            {
                if (index > 0)
                {
                    code.Append(" && ");
                }

                code.Append("higherPriority.")
                    .Append(SparseNaming.EscapeIdentifier(members[index].Property.Name))
                    .Append(".IsPresent");
            }

            code.AppendLine(")");
            code.AppendLineAt(3, "{");
            code.AppendLineAt(4, "return higherPriority;");
            code.AppendLineAt(3, "}");
        }

        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var lower = "this." + name;
            var higher = "higherPriority." + name;
            string expression;
            if (member.MergeStrategyType is not null)
            {
                expression = $"{MergeStrategyField(member)}.Merge({lower}, {higher})";
            }
            else if (member.MergeMode == 1 && member.ChildModel is not null)
            {
                expression =
                    $"{higher}.IsPresent ? {Optional}<{FragmentValueType(member)}>.Present(({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null) ? {lower}.Value!.Merge({higher}.Value!) : {higher}.Value) : {lower}";
            }
            else if (member.MergeMode is 2 or 3)
            {
                var merged = Expressions.BuildCollectionMerge(
                    member,
                    lower + ".Value!",
                    higher + ".Value!"
                );
                expression =
                    $"{higher}.IsPresent ? ({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null ? {Optional}<{FragmentValueType(member)}>.Present({merged}) : {higher}) : {lower}";
            }
            else
            {
                expression = $"{higher}.IsPresent ? {higher} : {lower}";
            }

            code.AppendIndent(4).Append(name).Append(" = ").Append(expression).AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    public void AppendApplyChanges(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members
    )
    {
        code.AppendLineAt(
            2,
            "/// <summary>Applies a sparse semantic diff to this contribution.</summary>"
        );
        code.AppendLineAt(2, "public Fragment ApplyChanges(Fragment changes)");
        code.AppendLineAt(2, "{");
        AppendNullGuard(code, 3, "changes");
        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var type = FragmentValueType(member);
            var expression = member.ChildModel is null
                ? $"changes.{name}.IsPresent ? changes.{name} : this.{name}"
                : $"changes.{name}.IsPresent ? {Optional}<{type}>.Present((this.{name}.IsPresent && (object?)this.{name}.Value is not null && (object?)changes.{name}.Value is not null) ? this.{name}.Value!.ApplyChanges(changes.{name}.Value!) : changes.{name}.Value) : this.{name}";
            code.AppendIndent(4).Append(name).Append(" = ").Append(expression).AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    public void AppendDiff(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool modelIsReferenceType
    )
    {
        foreach (var member in members.Where(static member => member.ChildModel is not null))
        {
            var type = member.ChildModel!.Value.NonNullableName;
            var fragment = member.ChildFragmentType!;
            code.AppendIndent(2)
                .Append("private static ")
                .Append(Optional)
                .Append("<")
                .Append(FragmentValueType(member))
                .Append("> __Diff_")
                .Append(member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('(');
            if (!member.ChildIsReferenceType)
            {
                code.Append(type).Append(" before, ").Append(type).AppendLine(" after)");
                code.AppendLineAt(2, "{");
                code.AppendIndent(3)
                    .Append("if (global::System.Collections.Generic.EqualityComparer<")
                    .Append(type)
                    .AppendLine(">.Default.Equals(before, after)) { return default; }");
                code.AppendIndent(3)
                    .Append("return ")
                    .Append(Optional)
                    .Append("<")
                    .Append(FragmentValueType(member))
                    .Append(">.Present(")
                    .Append(fragment)
                    .AppendLine(".Diff(before, after));");
                code.AppendLineAt(2, "}");
                continue;
            }

            code.Append(type).Append("? before, ").Append(type).AppendLine("? after)");
            code.AppendLineAt(2, "{");
            code.AppendLineAt(
                3,
                "if (global::System.Object.ReferenceEquals(before, after)) { return default; }"
            );
            code.AppendIndent(3)
                .Append("if (before is null || after is null) { return ")
                .Append(Optional)
                .Append("<")
                .Append(FragmentValueType(member))
                .Append(">.Present(after is null ? null : ")
                .Append(fragment)
                .AppendLine(".From(after)); }");
            code.AppendIndent(3)
                .Append("var difference = ")
                .Append(fragment)
                .AppendLine(".Diff(before, after);");
            code.AppendIndent(3)
                .Append("return difference.IsEmpty ? default : ")
                .Append(Optional)
                .Append("<")
                .Append(FragmentValueType(member))
                .AppendLine(">.Present(difference);");
            code.AppendLineAt(2, "}");
        }

        code.AppendLineAt(
            2,
            "/// <summary>Creates a sparse semantic diff between two ordinary model values.</summary>"
        );
        code.AppendIndent(2)
            .Append("public static Fragment Diff(")
            .Append(modelType)
            .Append(" before, ")
            .Append(modelType)
            .AppendLine(" after)");
        code.AppendLineAt(2, "{");
        if (modelIsReferenceType)
        {
            AppendNullGuard(code, 3, "before");
            AppendNullGuard(code, 3, "after");
        }

        code.AppendLineAt(3, "return new Fragment");
        code.AppendLineAt(3, "{");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var before = "before." + name;
            var after = "after." + name;
            var valueType = FragmentValueType(member);
            string condition;
            if (member.MergeStrategyType is not null)
            {
                condition =
                    $"{MergeStrategyField(member)}.AreEqual({before}, {after}) ? default : {Optional}<{valueType}>.Present({after})";
            }
            else if (member.ChildModel is null)
            {
                condition =
                    $"{Expressions.ValueEqualityExpression(member, before, after)} ? default : {Optional}<{valueType}>.Present({after})";
            }
            else
            {
                condition = $"__Diff_{member.Id}({before}, {after})";
            }

            code.AppendIndent(4).Append(name).Append(" = ").Append(condition).AppendLine(",");
        }

        code.AppendLineAt(3, "};");
        code.AppendLineAt(2, "}");
        code.AppendLine();
    }

    public void AppendFragmentClone(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        bool usesPocoCloning
    )
    {
        code.AppendLineAt(
            2,
            "/// <summary>Copies the fragment and its generated nested values.</summary>"
        );
        code.AppendLineAt(2, "public Fragment DeepClone()");
        code.AppendLineAt(2, "{");
        AppendCloneContext(code, 3);
        code.AppendLineAt(3, "return DeepClone(" + CloneContext + ");");
        code.AppendLineAt(2, "}");
        code.AppendLine();
        code.AppendIndent(2)
            .Append(
                "internal Fragment DeepClone(global::System.Collections.Generic.Dictionary<object, object> "
            )
            .Append(CloneContext)
            .AppendLine(")");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if ("
                + CloneContext
                + ".TryGetValue(this, out var existing)) return (Fragment)existing;"
        );
        code.AppendLineAt(3, "return new Fragment(this, " + CloneContext + ");");
        code.AppendLineAt(2, "}");
        code.AppendLine();
        code.AppendIndent(2)
            .Append(
                "private Fragment(Fragment source, global::System.Collections.Generic.Dictionary<object, object> "
            )
            .Append(CloneContext)
            .AppendLine(")");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, CloneContext + ".Add(source, this);");
        foreach (var member in members)
        {
            var name = SparseNaming.EscapeIdentifier(member.Property.Name);
            var type = FragmentValueType(member);
            var expression = Expressions.CloneFragmentExpression(
                member,
                "source." + name + ".Value"
            );
            code.AppendIndent(4)
                .Append("this.")
                .Append(name)
                .Append(" = source.")
                .Append(name)
                .Append(".IsPresent ? ")
                .Append(Optional)
                .Append("<")
                .Append(type)
                .Append(">.Present(")
                .Append(expression)
                .AppendLine(") : default;");
        }

        code.AppendLineAt(2, "}");
        code.AppendLine();
    }
}
