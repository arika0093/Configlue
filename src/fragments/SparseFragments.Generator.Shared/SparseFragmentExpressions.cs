namespace SparseFragments.Generator.Shared;

/// <summary>Shared expressions for model/fragment clones, semantic equality and built-in collection merging.</summary>
internal sealed class SparseFragmentExpressions(
    string cloneContext,
    string valueComparer = SparseWellKnownNames.ValueComparerType,
    string collectionMerger = SparseWellKnownNames.CollectionMergerType
)
{
    private string ValueComparer { get; } = valueComparer;
    private string CollectionMerger { get; } = collectionMerger;
    private string CloneContext { get; } = cloneContext;

    public string ValueEqualityExpression(SparseMemberModel member, string left, string right)
    {
        var collection = member.Collection;
        return collection.CloneKind switch
        {
            SparseCloneCollectionKind.Set =>
                $"{ValueComparer}.AreSetEqual<{collection.ElementType.Name}>({left}, {right})",
            SparseCloneCollectionKind.Dictionary when collection.ValueType is not null =>
                $"{ValueComparer}.AreDictionaryEqual<{collection.ElementType.Name}, {collection.ValueType.Value.Name}>({left}, {right})",
            _ => $"{ValueComparer}.AreEqual({left}, {right})",
        };
    }

    public string CloneValueExpression(SparseTypeModel type, string access)
    {
        if (type.IsFragmentModel)
        {
            return type.IsReferenceType
                ? $"{access} is null ? default! : (({type.Name}){access}).DeepClone({CloneContext})"
                : $"(({type.Name}){access}).DeepClone({CloneContext})";
        }

        var cloneHelperName = type.PocoCloneHelperName;
        if (cloneHelperName is not null)
        {
            return type.IsReferenceType
                ? $"{access} is null ? default! : {cloneHelperName}({access}, {CloneContext})"
                : $"{cloneHelperName}({access}, {CloneContext})";
        }

        return access;
    }

    public string CloneModelExpression(SparseMemberModel member, string access)
    {
        if (member.ChildModel is not null && !member.ChildIsStructural)
        {
            return member.ChildIsReferenceType
                ? $"{access} is null ? null! : {access}.DeepClone({CloneContext})"
                : $"(({member.Property.Type.Name}){access}).DeepClone({CloneContext})";
        }

        var cloneHelperName = member.Property.Type.PocoCloneHelperName;
        if (cloneHelperName is not null)
        {
            return member.Property.Type.IsReferenceType
                ? $"{access} is null ? null! : {cloneHelperName}({access}, {CloneContext})"
                : $"{cloneHelperName}({access}, {CloneContext})";
        }

        var cloned = CloneCollectionExpression(member, access);
        return member.Property.Type.IsReferenceType
            ? $"{access} is null ? null! : {cloned}"
            : cloned;
    }

    public string CloneFragmentExpression(SparseMemberModel member, string access)
    {
        if (member.ChildModel is not null)
        {
            return $"{access}?.DeepClone({CloneContext})";
        }

        var cloneHelperName = member.Property.Type.PocoCloneHelperName;
        if (cloneHelperName is not null)
        {
            return $"{access} is null ? null : {cloneHelperName}({access}!, {CloneContext})";
        }

        var cloned = CloneCollectionExpression(member, access + "!");
        return $"(object?){access} is null ? default : {cloned}";
    }

    public string CloneCollectionExpression(SparseMemberModel member, string access)
    {
        var collection = member.Collection;
        if (collection.CloneKind == SparseCloneCollectionKind.Unsupported)
        {
            return access;
        }

        var elementType = collection.ElementType.Name;

        if (collection.ValueType is not null)
        {
            if (collection.CloneKind == SparseCloneCollectionKind.Dictionary)
            {
                var keySelector = CloneSelector(collection.ElementType, "key");
                var valueSelector = CloneSelector(collection.ValueType.Value, "value");
                return $"__CloneDictionary<{collection.ElementType.Name}, {collection.ValueType.Value.Name}, {member.Property.Type.Name}>({access}, {CloneContext}, {keySelector}, {valueSelector})";
            }

            return access;
        }

        var selector = CloneSelector(collection.ElementType, "item");
        return collection.CloneKind switch
        {
            SparseCloneCollectionKind.Array =>
                $"__CloneArray<{elementType}, {member.Property.Type.Name}>({access}, {CloneContext}, {selector})",
            SparseCloneCollectionKind.List =>
                $"__CloneList<{elementType}, {member.Property.Type.Name}>({access}, {CloneContext}, {selector})",
            SparseCloneCollectionKind.Set => member.PortableSetView
            && IsInterfaceSet(collection.NamedTypeDefinition)
                ? $"__CloneSetView<{elementType}, {member.Property.Type.Name}>({access}, {CloneContext}, {selector})"
                : $"__CloneSet<{elementType}, {member.Property.Type.Name}>({access}, {CloneContext}, {selector})",
            _ => access,
        };
    }

    // A null selector means the existing clone policy keeps each element unchanged.
    // Recursive elements continue to receive their context-aware clone delegate.
    private string CloneSelector(SparseTypeModel type, string parameter)
    {
        var expression = CloneValueExpression(type, parameter);
        return expression == parameter ? "null" : $"{parameter} => {expression}";
    }

    private static bool IsInterfaceSet(string? namedTypeDefinition) =>
        namedTypeDefinition
            is SparseWellKnownNames.InterfaceSetTypeDefinition
                or SparseWellKnownNames.ReadOnlySetTypeDefinition;

    public string BuildCollectionMerge(SparseMemberModel member, string lower, string higher)
    {
        var elementType = member.Collection.ElementType.Name;
        if (member.Collection.Kind == SparseCollectionKind.Set)
        {
            return $"{CollectionMerger}.MergeSet<{elementType}>({lower}, {higher})";
        }

        var combined = $"global::System.Linq.Enumerable.Concat({lower}, {higher})";
        if (member.MergeMode == 3)
        {
            var method =
                member.Collection.Kind == SparseCollectionKind.List
                    ? "MergeDistinctList"
                    : "MergeDistinctArray";
            return $"{CollectionMerger}.{method}<{elementType}>({lower}, {higher})";
        }

        return member.Collection.Kind switch
        {
            SparseCollectionKind.List =>
                $"new global::System.Collections.Generic.List<{elementType}>({combined})",
            _ => $"global::System.Linq.Enumerable.ToArray({combined})",
        };
    }

    /// <summary>Builds an expression that materializes a sequence of elements into the member's collection type.</summary>
    /// <remarks>
    /// Narrowed (issue #280): sets normalize to <c>HashSet{T}</c>, sequences to
    /// arrays/lists. Sorted, observable, read-only-wrapper, queue/stack,
    /// concurrent and immutable shapes are unsupported and handled as
    /// <c>Unsupported</c> by the analyzer.
    /// </remarks>
    public static string MaterializeCollection(SparseMemberModel member, string elements)
    {
        var elementType = member.Collection.ElementType.Name;
        return member.Collection.CloneKind switch
        {
            SparseCloneCollectionKind.Set =>
                $"new global::System.Collections.Generic.HashSet<{elementType}>({elements})",
            SparseCloneCollectionKind.Array =>
                $"global::System.Linq.Enumerable.ToArray({elements})",
            _ => $"new global::System.Collections.Generic.List<{elementType}>({elements})",
        };
    }
}
