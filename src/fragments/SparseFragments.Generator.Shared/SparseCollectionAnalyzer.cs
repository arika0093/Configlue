using System.Collections.Generic;
using System.Linq;
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
/// <remarks>
/// Intentionally narrow (issue #280): first-class configuration-model shapes are
/// arrays, lists, sets and dictionaries. Stateful or specialized BCL containers
/// (queues, stacks, concurrent collections, <c>BlockingCollection</c>,
/// <c>PriorityQueue</c>, <c>LinkedList</c>, sorted/observable/read-only wrappers
/// and immutable collections) are <see cref="Unsupported"/> and require an
/// explicit custom clone/merge policy.
/// </remarks>
internal enum SparseCloneCollectionKind
{
    Unsupported,
    Array,
    List,
    Set,
    Dictionary,
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
    public static bool HashSetSupportsCapacity(Compilation compilation) =>
        compilation
            .GetTypeByMetadataName("System.Collections.Generic.HashSet`1")
            ?.InstanceConstructors.Any(static constructor =>
                constructor.DeclaredAccessibility == Accessibility.Public
                && constructor.Parameters.Length == 2
                && constructor.Parameters[0].Type.SpecialType == SpecialType.System_Int32
                && constructor.Parameters[1].Type is INamedTypeSymbol comparer
                && comparer.OriginalDefinition.ToDisplayString()
                    == "System.Collections.Generic.IEqualityComparer<T>"
            ) == true;

    public static bool HashSetImplementsReadOnlySet(Compilation compilation)
    {
        var readOnlySet = compilation.GetTypeByMetadataName(
            "System.Collections.Generic.IReadOnlySet`1"
        );
        if (readOnlySet is null)
            return false;
        var hashSet = compilation.GetTypeByMetadataName("System.Collections.Generic.HashSet`1");
        if (hashSet is null)
            return false;
        var constructed = hashSet.Construct(compilation.GetSpecialType(SpecialType.System_Int32));
        return constructed.AllInterfaces.Any(implemented =>
            SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, readOnlySet)
        );
    }

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
            or "System.Collections.Generic.SortedDictionary<TKey, TValue>"
            or "System.Collections.Generic.SortedList<TKey, TValue>"
            or "System.Collections.Generic.IDictionary<TKey, TValue>"
            or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>" =>
                SparseCloneCollectionKind.Dictionary,
            // Narrowed (issue #280): Queue/Stack, concurrent collections,
            // BlockingCollection, PriorityQueue, LinkedList, SortedSet,
            // ObservableCollection/ReadOnlyCollection and immutable collections
            // are intentionally unsupported. Use an array, List, HashSet or
            // Dictionary shape, or provide a custom clone/merge policy.
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
