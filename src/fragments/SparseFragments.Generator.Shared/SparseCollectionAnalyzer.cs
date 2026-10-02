using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace SparseFragments.Generator.Shared;

/// <summary>Merge-relevant collection category used by generated fragments.</summary>
internal enum SparseCollectionKind
{
    Unsupported,
    Array,
    List,
    Set,
}

/// <summary>Clone-relevant collection category used by generated fragments.</summary>
internal enum SparseCloneCollectionKind
{
    Unsupported,
    Array,
    List,
    Set,
    Dictionary,
    Queue,
    Stack,
    ConcurrentQueue,
    ConcurrentStack,
    BlockingCollection,
    PriorityQueue,
    LinkedList,
    SortedSet,
    ObservableCollection,
    ReadOnlyCollection,
    ImmutableArray,
    ImmutableList,
    ImmutableSet,
    ImmutableDictionary,
}

/// <summary>Describes a discovered collection type.</summary>
internal sealed class SparseSymbolCollectionInfo(
    SparseCollectionKind kind,
    SparseCloneCollectionKind cloneKind,
    ITypeSymbol elementType,
    ITypeSymbol? valueType,
    INamedTypeSymbol? namedType
)
{
    public SparseCollectionKind Kind { get; } = kind;
    public SparseCloneCollectionKind CloneKind { get; } = cloneKind;
    public ITypeSymbol ElementType { get; } = elementType;
    public ITypeSymbol? ValueType { get; } = valueType;
    public INamedTypeSymbol? NamedType { get; } = namedType;
    public static SparseSymbolCollectionInfo Unsupported { get; } =
        new(
            SparseCollectionKind.Unsupported,
            SparseCloneCollectionKind.Unsupported,
            null!,
            null,
            null
        );
}

/// <summary>Discovers merge and clone semantics for candidate member types.</summary>
internal static class SparseCollectionAnalyzer
{
    public static SparseSymbolCollectionInfo GetCollectionInfo(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return new SparseSymbolCollectionInfo(
                SparseCollectionKind.Array,
                SparseCloneCollectionKind.Array,
                array.ElementType,
                null,
                null
            );
        }

        if (type is not INamedTypeSymbol named || named.TypeArguments.Length is < 1 or > 2)
        {
            return SparseSymbolCollectionInfo.Unsupported;
        }

        var elementType = named.TypeArguments[0];
        var definition = named.ConstructedFrom.ToDisplayString();
        var kind = definition switch
        {
            "System.Collections.Generic.List<T>" => SparseCollectionKind.List,
            "System.Collections.Generic.IEnumerable<T>"
            or "System.Collections.Generic.IReadOnlyCollection<T>"
            or "System.Collections.Generic.IReadOnlyList<T>" => SparseCollectionKind.Array,
            "System.Collections.Generic.HashSet<T>"
            or "System.Collections.Generic.ISet<T>"
            or "System.Collections.Generic.IReadOnlySet<T>" => SparseCollectionKind.Set,
            _ => SparseCollectionKind.Unsupported,
        };

        var cloneKind = definition switch
        {
            "System.Collections.Generic.List<T>" or "System.Collections.Generic.IList<T>" =>
                SparseCloneCollectionKind.List,
            "System.Collections.Generic.IEnumerable<T>"
            or "System.Collections.Generic.IReadOnlyCollection<T>"
            or "System.Collections.Generic.IReadOnlyList<T>" => SparseCloneCollectionKind.Array,
            "System.Collections.Generic.HashSet<T>"
            or "System.Collections.Generic.ISet<T>"
            or "System.Collections.Generic.IReadOnlySet<T>" => SparseCloneCollectionKind.Set,
            "System.Collections.Generic.Dictionary<TKey, TValue>"
            or "System.Collections.Generic.IDictionary<TKey, TValue>"
            or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>" =>
                SparseCloneCollectionKind.Dictionary,
            "System.Collections.Generic.Queue<T>" => SparseCloneCollectionKind.Queue,
            "System.Collections.Generic.Stack<T>" => SparseCloneCollectionKind.Stack,
            "System.Collections.Concurrent.ConcurrentQueue<T>" =>
                SparseCloneCollectionKind.ConcurrentQueue,
            "System.Collections.Concurrent.ConcurrentStack<T>" =>
                SparseCloneCollectionKind.ConcurrentStack,
            "System.Collections.Concurrent.BlockingCollection<T>" =>
                SparseCloneCollectionKind.BlockingCollection,
            "System.Collections.Generic.LinkedList<T>" => SparseCloneCollectionKind.LinkedList,
            "System.Collections.Generic.SortedSet<T>" => SparseCloneCollectionKind.SortedSet,
            "System.Collections.Generic.PriorityQueue<TElement, TPriority>" =>
                SparseCloneCollectionKind.PriorityQueue,
            "System.Collections.ObjectModel.ObservableCollection<T>" =>
                SparseCloneCollectionKind.ObservableCollection,
            "System.Collections.ObjectModel.ReadOnlyCollection<T>" =>
                SparseCloneCollectionKind.ReadOnlyCollection,
            "System.Collections.Immutable.ImmutableArray<T>" =>
                SparseCloneCollectionKind.ImmutableArray,
            "System.Collections.Immutable.ImmutableList<T>" =>
                SparseCloneCollectionKind.ImmutableList,
            "System.Collections.Immutable.ImmutableHashSet<T>" =>
                SparseCloneCollectionKind.ImmutableSet,
            "System.Collections.Immutable.ImmutableDictionary<TKey, TValue>" =>
                SparseCloneCollectionKind.ImmutableDictionary,
            _ => SparseCloneCollectionKind.Unsupported,
        };

        return new SparseSymbolCollectionInfo(
            kind,
            cloneKind,
            elementType,
            named.TypeArguments.Length == 2 ? named.TypeArguments[1] : null,
            named
        );
    }
}
