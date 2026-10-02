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
            SparseCloneCollectionKind.Set
            or SparseCloneCollectionKind.SortedSet
            or SparseCloneCollectionKind.ImmutableSet =>
                $"{ValueComparer}.AreSetEqual<{collection.ElementType.Name}>({left}, {right})",
            SparseCloneCollectionKind.Dictionary
            or SparseCloneCollectionKind.ImmutableDictionary
                when collection.ValueType is not null =>
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
            return $"{access}?.DeepClone()";
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
        var elements = access;
        if (
            collection.ElementType.IsFragmentModel
            || collection.ElementType.PocoCloneHelperName is not null
        )
        {
            elements =
                $"global::System.Linq.Enumerable.Select({access}, item => {CloneValueExpression(collection.ElementType, "item")})";
        }

        if (collection.ValueType is not null)
        {
            if (collection.CloneKind == SparseCloneCollectionKind.PriorityQueue)
            {
                var priorityType = collection.ValueType.Value.Name;
                var elementSelector = CloneValueExpression(collection.ElementType, "item.Element");
                var prioritySelector = CloneValueExpression(
                    collection.ValueType.Value,
                    "item.Priority"
                );
                var entries =
                    $"global::System.Linq.Enumerable.Select({access}.UnorderedItems, item => ({elementSelector}, {prioritySelector}))";
                return $"new global::System.Collections.Generic.PriorityQueue<{elementType}, {priorityType}>({entries}, {access}.Comparer)";
            }

            if (collection.CloneKind == SparseCloneCollectionKind.Dictionary)
            {
                var keySelector = $"key => {CloneValueExpression(collection.ElementType, "key")}";
                var valueSelector =
                    $"value => {CloneValueExpression(collection.ValueType.Value, "value")}";
                return $"__CloneDictionary<{collection.ElementType.Name}, {collection.ValueType.Value.Name}, {member.Property.Type.Name}>({access}, {CloneContext}, {keySelector}, {valueSelector})";
            }

            if (collection.CloneKind == SparseCloneCollectionKind.ImmutableDictionary)
            {
                var keySelector =
                    $"pair => {CloneValueExpression(collection.ElementType, "pair.Key")}";
                var valueSelector =
                    $"pair => {CloneValueExpression(collection.ValueType.Value, "pair.Value")}";
                return $"global::System.Collections.Immutable.ImmutableDictionary.ToImmutableDictionary({access}, {keySelector}, {valueSelector}, {access}.KeyComparer, {access}.ValueComparer)";
            }

            return access;
        }

        return collection.CloneKind switch
        {
            SparseCloneCollectionKind.Array =>
                $"__CloneArray<{elementType}, {member.Property.Type.Name}>({access}, {CloneContext}, item => {CloneValueExpression(collection.ElementType, "item")})",
            SparseCloneCollectionKind.List =>
                $"__CloneList<{elementType}, {member.Property.Type.Name}>({access}, {CloneContext}, item => {CloneValueExpression(collection.ElementType, "item")})",
            SparseCloneCollectionKind.Set or SparseCloneCollectionKind.SortedSet =>
                $"__CloneSet<{elementType}, {member.Property.Type.Name}>({access}, {CloneContext}, item => {CloneValueExpression(collection.ElementType, "item")})",
            SparseCloneCollectionKind.ImmutableSet => CloneSetExpression(
                collection,
                access,
                elements,
                elementType
            ),
            SparseCloneCollectionKind.Queue =>
                $"new global::System.Collections.Generic.Queue<{elementType}>({elements})",
            SparseCloneCollectionKind.Stack =>
                $"new global::System.Collections.Generic.Stack<{elementType}>(global::System.Linq.Enumerable.Reverse({elements}))",
            SparseCloneCollectionKind.ConcurrentQueue =>
                $"new global::System.Collections.Concurrent.ConcurrentQueue<{elementType}>({elements})",
            SparseCloneCollectionKind.ConcurrentStack =>
                $"new global::System.Collections.Concurrent.ConcurrentStack<{elementType}>(global::System.Linq.Enumerable.Reverse({elements}))",
            SparseCloneCollectionKind.BlockingCollection =>
                $"__CloneBlockingCollection({access}, {elements})",
            SparseCloneCollectionKind.LinkedList =>
                $"new global::System.Collections.Generic.LinkedList<{elementType}>({elements})",
            SparseCloneCollectionKind.ObservableCollection =>
                $"new global::System.Collections.ObjectModel.ObservableCollection<{elementType}>({elements})",
            SparseCloneCollectionKind.ReadOnlyCollection =>
                $"new global::System.Collections.ObjectModel.ReadOnlyCollection<{elementType}>(new global::System.Collections.Generic.List<{elementType}>({elements}))",
            SparseCloneCollectionKind.ImmutableArray =>
                $"{access}.IsDefault ? {access} : global::System.Collections.Immutable.ImmutableArray.CreateRange({elements})",
            SparseCloneCollectionKind.ImmutableList =>
                $"global::System.Collections.Immutable.ImmutableList.CreateRange({elements})",
            _ => access,
        };
    }

    private static string CloneSetExpression(
        SparseCollectionInfo collection,
        string access,
        string elements,
        string elementType
    )
    {
        var definition = collection.NamedTypeDefinition;
        return definition switch
        {
            "System.Collections.Generic.HashSet<T>" =>
                $"new global::System.Collections.Generic.HashSet<{elementType}>({elements}, {access}.Comparer)",
            "System.Collections.Generic.SortedSet<T>" =>
                $"new global::System.Collections.Generic.SortedSet<{elementType}>({elements}, {access}.Comparer)",
            "System.Collections.Immutable.ImmutableHashSet<T>" =>
                $"global::System.Collections.Immutable.ImmutableHashSet.CreateRange({access}.KeyComparer, {elements})",
            _ =>
                $"new global::System.Collections.Generic.HashSet<{elementType}>({elements}, ({access} as global::System.Collections.Generic.HashSet<{elementType}>)?.Comparer)",
        };
    }

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
            combined = $"global::System.Linq.Enumerable.Distinct({combined})";
        }

        return member.Collection.Kind switch
        {
            SparseCollectionKind.List =>
                $"new global::System.Collections.Generic.List<{elementType}>({combined})",
            _ => $"global::System.Linq.Enumerable.ToArray({combined})",
        };
    }
}
