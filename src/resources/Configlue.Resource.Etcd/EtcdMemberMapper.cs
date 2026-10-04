using System.Text;
using System.Text.Json;
using Configlue.CompilerServices;

namespace Configlue.Resource.Etcd;

/// <summary>One leaf member contribution: its generated path, etcd suffix, and value type.</summary>
internal sealed record EtcdLeafDescriptor(
    string[] PathNames,
    int[] PathIds,
    Type LeafType,
    string MemberSuffix
);

/// <summary>One flattened leaf assignment from a fragment write.</summary>
internal sealed record EtcdLeafAssignment(string MemberSuffix, object? Value, Type LeafType);

/// <summary>
/// Maps generated member paths to etcd key suffixes using schema metadata and fragment
/// operations only. No reflection-only traversal is used: member identity flows from
/// <see cref="ConfiglueModelSchema"/> members and generated fragment accessors.
/// </summary>
internal sealed class EtcdMemberMapper
{
    private readonly Dictionary<string, EtcdLeafDescriptor> _suffixMap = new(
        StringComparer.Ordinal
    );

    public EtcdMemberMapper(ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;
        var leaves = new List<EtcdLeafDescriptor>();
        CollectLeaves(schema, [], [], new HashSet<Type>(), leaves);
        Leaves = leaves.AsReadOnly();
        foreach (var leaf in leaves)
        {
            _suffixMap[leaf.MemberSuffix] = leaf;
        }
    }

    public ConfiglueModelSchema Schema { get; }

    public IReadOnlyList<EtcdLeafDescriptor> Leaves { get; }

    public bool TryGetLeaf(string memberSuffix, out EtcdLeafDescriptor? leaf) =>
        _suffixMap.TryGetValue(memberSuffix, out leaf);

    public List<EtcdLeafAssignment> FlattenFragment(IConfiglueFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var assignments = new List<EtcdLeafAssignment>();
        FlattenFragmentCore(fragment, Schema, [], assignments);
        assignments.Sort(
            static (left, right) =>
                string.Compare(left.MemberSuffix, right.MemberSuffix, StringComparison.Ordinal)
        );
        return assignments;
    }

    public IConfiglueFragment BuildFragment(IReadOnlyList<EtcdStoredLeaf> storedLeaves)
    {
        var root = new LeafNode();
        foreach (var stored in storedLeaves)
        {
            if (!TryGetLeaf(stored.MemberSuffix, out var descriptor) || descriptor is null)
            {
                continue;
            }

            var parsed = DeserializeLeaf(stored.Value, descriptor.LeafType, stored.Key);
            root.Add(descriptor.PathNames, 0, parsed);
        }

        return BuildFragmentCore(Schema, root, string.Empty);
    }

    private static void CollectLeaves(
        ConfiglueModelSchema schema,
        string[] parentNames,
        int[] parentIds,
        HashSet<Type> ancestors,
        List<EtcdLeafDescriptor> leaves
    )
    {
        if (!ancestors.Add(schema.ModelType))
        {
            return;
        }

        try
        {
            foreach (var member in schema.Members)
            {
                var pathNames = Append(parentNames, member.Name);
                var pathIds = Append(parentIds, member.Id);
                if (member.NestedSchemaFactory is { } nestedFactory)
                {
                    CollectLeaves(nestedFactory(), pathNames, pathIds, ancestors, leaves);
                    continue;
                }

                leaves.Add(
                    new EtcdLeafDescriptor(
                        pathNames,
                        pathIds,
                        member.ValueType,
                        EtcdKeyEncoding.EncodeMemberSuffix(pathNames)
                    )
                );
            }
        }
        finally
        {
            ancestors.Remove(schema.ModelType);
        }
    }

    private static void FlattenFragmentCore(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        string[] parentNames,
        List<EtcdLeafAssignment> assignments
    )
    {
        foreach (var member in fragment.EnumeratePresentMembersFast())
        {
            if (!schema.TryGetMember(member.Id, out var memberSchema))
            {
                throw new FormatException(
                    $"Fragment '{fragment.GetType()}' contains an unknown member ID {member.Id} for schema '{schema.Id}'."
                );
            }

            if (memberSchema.NestedSchemaFactory is { } nestedFactory)
            {
                if (member.Value is not IConfiglueFragment nestedFragment)
                {
                    throw new FormatException(
                        $"Member '{memberSchema.Name}' names a nested model but carries no fragment."
                    );
                }

                FlattenFragmentCore(
                    nestedFragment,
                    nestedFactory(),
                    Append(parentNames, memberSchema.Name),
                    assignments
                );
                continue;
            }

            var pathNames = Append(parentNames, memberSchema.Name);
            assignments.Add(
                new EtcdLeafAssignment(
                    EtcdKeyEncoding.EncodeMemberSuffix(pathNames),
                    member.Value,
                    memberSchema.ValueType
                )
            );
        }
    }

    private static IConfiglueFragment BuildFragmentCore(
        ConfiglueModelSchema schema,
        LeafNode node,
        string parentPath
    )
    {
        var fragment = schema.CreateEmptyFragment();
        foreach (var member in schema.Members)
        {
            if (!node.Children.TryGetValue(member.Name, out var child))
            {
                continue;
            }

            var propertyPath = string.IsNullOrEmpty(parentPath)
                ? member.Name
                : parentPath + "." + member.Name;
            if (member.NestedSchemaFactory is { } nestedFactory)
            {
                if (child.ValueIsSet)
                {
                    throw new FormatException(
                        $"The etcd key for '{propertyPath}' carries a scalar value for a nested model."
                    );
                }

                fragment = fragment.WithMember(
                    member.Id,
                    BuildFragmentCore(nestedFactory(), child, propertyPath)
                );
                continue;
            }

            if (!child.ValueIsSet)
            {
                throw new FormatException(
                    $"The etcd key for '{propertyPath}' continues past non-nested member '{member.Name}'."
                );
            }

            fragment = fragment.WithMember(member.Id, child.Value);
        }

        return fragment;
    }

    private static string[] Append(string[] parent, string name)
    {
        var result = new string[parent.Length + 1];
        Array.Copy(parent, result, parent.Length);
        result[parent.Length] = name;
        return result;
    }

    private static int[] Append(int[] parent, int id)
    {
        var result = new int[parent.Length + 1];
        Array.Copy(parent, result, parent.Length);
        result[parent.Length] = id;
        return result;
    }

    internal static byte[] SerializeLeaf(object? value, Type leafType, string key)
    {
        ArgumentNullException.ThrowIfNull(leafType);
        if (leafType == typeof(string))
        {
            if (value is not null && value is not string)
            {
                throw new FormatException(
                    $"The etcd key '{key}' carries '{value.GetType()}' but member requires '{leafType}'."
                );
            }

            return Encoding.UTF8.GetBytes((string?)value ?? string.Empty);
        }

        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, leafType);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"The value for etcd key '{key}' cannot be serialized.",
                exception
            );
        }
    }

    internal static object? DeserializeLeaf(ReadOnlyMemory<byte> value, Type leafType, string key)
    {
        try
        {
            if (leafType == typeof(string))
            {
                return Encoding.UTF8.GetString(value.ToArray());
            }

            return JsonSerializer.Deserialize(value.Span, leafType);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"The etcd key '{key}' carries a malformed value.",
                exception
            );
        }
    }

    private sealed class LeafNode
    {
        public readonly Dictionary<string, LeafNode> Children = new(StringComparer.Ordinal);

        public object? Value;

        public bool ValueIsSet;

        public void Add(string[] path, int index, object? value)
        {
            if (index == path.Length)
            {
                Value = value;
                ValueIsSet = true;
                return;
            }

            if (!Children.TryGetValue(path[index], out var child))
            {
                child = new LeafNode();
                Children[path[index]] = child;
            }

            child.Add(path, index + 1, value);
        }
    }
}

/// <summary>One mapped key-value pair decoded from an etcd prefix read.</summary>
internal sealed record EtcdStoredLeaf(string Key, string MemberSuffix, ReadOnlyMemory<byte> Value);
