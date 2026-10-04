using Configlue.CompilerServices;

namespace Configlue.DocumentEditing;

// Shared semantic edit plan for JSONC/YAML preservation. Ownership, child
// schema, and object ops via stable schema identity; rendering stays
// format-local. Collection/sequence slicing and section-path positions stay
// format-local: sharing them merely relocates complexity.
// No reflection walker, no parser unification, no shared DOM.
internal static class DocumentSemanticEditPlan
{
    internal static string CacheKey(ConfiglueModelSchema schema) =>
        string.Concat(
            schema.Id,
            "\0",
            schema.Version,
            "\0",
            schema.ModelType.FullName ?? schema.ModelType.Name
        );

    internal static IConfiglueFragment CreateShallowPresentFragment(ConfiglueModelSchema schema)
    {
        var fragment = schema.CreateEmptyFragment();
        foreach (var member in schema.Members)
        {
            object? value = null;
            if (member.NestedSchemaFactory is null)
            {
                value = member.DefaultValueFactory?.Invoke();
                if (value is null && member.ValueType.IsValueType)
                    value = Activator.CreateInstance(member.ValueType);
            }
            fragment = fragment.WithMember(member.Id, value);
        }
        return fragment;
    }

    internal static ConfiglueModelSchema? ResolveChildSchema(
        ConfiglueModelSchema? currentSchema,
        ConfiglueModelSchema? rootSchema,
        string wireName,
        ConfiglueModelSchema? nestedCandidate,
        bool shapeIsNullOrPlaceholder,
        bool shapeIsObjectLike,
        out bool needsBareShape
    )
    {
        needsBareShape = false;
        if (currentSchema is null || rootSchema is null)
            return null;
        if (
            string.Equals(wireName, "$value", StringComparison.Ordinal)
            && ReferenceEquals(currentSchema, rootSchema)
            && shapeIsObjectLike
        )
            return currentSchema;
        if (nestedCandidate is null)
            return null;
        if (shapeIsNullOrPlaceholder)
            needsBareShape = true;
        return nestedCandidate;
    }

    internal static void DiffObjects(
        IReadOnlyList<string> currentNames,
        IReadOnlyList<string> updatedNames,
        HashSet<string>? shapeNames,
        bool hasSchemaShape,
        Action<int> onRemove,
        Action<int, int, string> onMatch,
        Action<int[], int[]> onAdd
    )
    {
        var updatedIndexByName = new Dictionary<string, int>(
            updatedNames.Count,
            StringComparer.Ordinal
        );
        for (var index = 0; index < updatedNames.Count; index++)
            updatedIndexByName.Add(updatedNames[index], index);
        var currentNameSet = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < currentNames.Count; index++)
            currentNameSet.Add(currentNames[index]);
        var retained = new List<int>(currentNames.Count);
        for (var index = 0; index < currentNames.Count; index++)
        {
            var wireName = currentNames[index];
            if (!updatedIndexByName.TryGetValue(wireName, out var updatedIndex))
            {
                bool owned = !hasSchemaShape
                    ? shapeNames is null || shapeNames.Contains(wireName)
                    : shapeNames is not null && shapeNames.Contains(wireName);
                if (owned)
                    onRemove(index);
                else
                    retained.Add(index);
                continue;
            }
            retained.Add(index);
            onMatch(index, updatedIndex, wireName);
        }
        var additions = new List<int>(updatedNames.Count);
        for (var index = 0; index < updatedNames.Count; index++)
            if (!currentNameSet.Contains(updatedNames[index]))
                additions.Add(index);
        onAdd(additions.ToArray(), retained.ToArray());
    }
}
