namespace Configlue.CompilerServices;

/// <summary>An immutable generated member-ID sequence qualified by its root schema.</summary>
/// <remarks>Generated-code plumbing: hand-written code uses property-path strings instead.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public readonly struct ConfiglueMemberPath : IEquatable<ConfiglueMemberPath>
{
    private readonly ConfiglueModelSchema? _root;
    private readonly int[]? _ids;

    private ConfiglueMemberPath(ConfiglueModelSchema root, int[] ids)
    {
        _root = root;
        _ids = ids;
    }

    /// <summary>The root model schema that qualifies otherwise schema-local IDs.</summary>
    public ConfiglueModelSchema RootSchema =>
        _root ?? throw new InvalidOperationException("The member path is uninitialized.");

    /// <summary>The number of member segments.</summary>
    public int Length => _ids?.Length ?? 0;

    /// <summary>The immutable sequence of generated IDs, interpreted within <see cref="RootSchema"/>.</summary>
    /// <remarks>Member IDs are version-local ordinals; the root schema qualifies the sequence.</remarks>
    public ReadOnlySpan<int> MemberIds => _ids;

    /// <summary>Starts a generated path for the supplied root schema.</summary>
    public static ConfiglueMemberPath Root(ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new(schema, []);
    }

    /// <summary>Appends a member ID generated within the current nested schema.</summary>
    public ConfiglueMemberPath Append(int memberId)
    {
        var ids = new int[Length + 1];
        MemberIds.CopyTo(ids);
        ids[^1] = memberId;
        return new(RootSchema, ids);
    }

    /// <summary>Compiles a diagnostic property path once at registration.</summary>
    public static ConfiglueMemberPath FromNames(ConfiglueModelSchema schema, string propertyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        var current = schema;
        var names = propertyPath.Split('.');
        var ids = new int[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            ConfiglueMemberSchema? found = null;
            foreach (var member in current.Members)
            {
                if (string.Equals(member.Name, names[index], StringComparison.Ordinal))
                {
                    found = member;
                    break;
                }
            }
            var selected =
                found
                ?? throw new ArgumentException(
                    $"Property path '{propertyPath}' contains unknown member '{names[index]}' in '{current.Id}'.",
                    nameof(propertyPath)
                );
            ids[index] = selected.Id;
            if (index < names.Length - 1)
            {
                current =
                    selected.NestedSchemaFactory?.Invoke()
                    ?? throw new ArgumentException(
                        $"Property path '{propertyPath}' continues through non-nested member '{selected.Name}'.",
                        nameof(propertyPath)
                    );
            }
        }
        return new(schema, ids);
    }

    /// <summary>Compares root identity and member IDs without parsing property names.</summary>
    public bool IsPrefixOf(ConfiglueMemberPath other)
    {
        return SameRoot(other)
            && Length <= other.Length
            && MemberIds.SequenceEqual(other.MemberIds[..Length]);
    }

    internal bool SameRoot(ConfiglueMemberPath other) =>
        _root?.ModelType == other._root?.ModelType
        && _root?.Id == other._root?.Id
        && _root?.Version == other._root?.Version;

    /// <inheritdoc />
    public bool Equals(ConfiglueMemberPath other) => Length == other.Length && IsPrefixOf(other);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ConfiglueMemberPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = _root?.ModelType?.GetHashCode() ?? 0;
        hash = unchecked(hash * 31 + (_root?.Id?.GetHashCode() ?? 0));
        hash = unchecked(hash * 31 + (_root?.Version.GetHashCode() ?? 0));
        foreach (var id in MemberIds)
        {
            hash = unchecked(hash * 31 + id.GetHashCode());
        }
        return hash;
    }

    /// <summary>Looks up the leaf's generated metadata.</summary>
    public ConfiglueMemberSchema ResolveMember()
    {
        var schema = RootSchema;
        ConfiglueMemberSchema member = default;
        for (var index = 0; index < Length; index++)
        {
            member = FindMember(schema, MemberIds[index]);
            if (index < Length - 1)
            {
                schema =
                    member.NestedSchemaFactory?.Invoke()
                    ?? throw new InvalidOperationException(
                        "A generated path traverses a non-nested member."
                    );
            }
        }
        return member;
    }

    /// <summary>Reads a sparse contribution by generated member IDs.</summary>
    public bool TryGetFragmentValue(IConfiglueFragment? fragment, out object? value)
    {
        value = null;
        for (var index = 0; index < Length; index++)
        {
            if (fragment is null)
            {
                return false;
            }
            var found = false;
            foreach (var member in fragment.EnumeratePresentMembersFast())
            {
                if (member.Id == MemberIds[index])
                {
                    value = member.Value;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
            if (index < Length - 1)
            {
                fragment = value as IConfiglueFragment;
            }
        }
        return Length > 0;
    }

    /// <summary>Reads a resolved value using generated getters.</summary>
    public object? GetModelValue(object model, out ConfiglueMemberSchema leaf)
    {
        var schema = RootSchema;
        object? value = model;
        leaf = default;
        for (var index = 0; index < Length; index++)
        {
            leaf = FindMember(schema, MemberIds[index]);
            value = value is null
                ? null
                : (
                    leaf.GetValue
                    ?? throw new InvalidOperationException("Generated getter metadata is missing.")
                )(value);
            if (index < Length - 1)
            {
                schema =
                    leaf.NestedSchemaFactory?.Invoke()
                    ?? throw new InvalidOperationException(
                        "A generated path traverses a non-nested member."
                    );
            }
        }
        return value;
    }

    private static ConfiglueMemberSchema FindMember(ConfiglueModelSchema schema, int id) =>
        schema.GetMember(id);

    /// <summary>Formats names only for diagnostics; identity comparisons always use IDs.</summary>
    public override string ToString()
    {
        var schema = RootSchema;
        var names = new string[Length];
        for (var index = 0; index < Length; index++)
        {
            var member = FindMember(schema, MemberIds[index]);
            names[index] = member.Name;
            if (index < Length - 1)
            {
                schema = member.NestedSchemaFactory!();
            }
        }
        return string.Join(".", names);
    }
}
