using System.ComponentModel;

namespace SparseFragments;

/// <summary>Describes one member of a generated sparse fragment schema.</summary>
/// <remarks>Advanced tooling vocabulary: describes generated fragment metadata.</remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public readonly record struct SparseFragmentMemberSchema
{
    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Id">The schema-local member ordinal.</param>
    /// <param name="Name">The source member name.</param>
    /// <param name="ValueType">The member value type.</param>
    /// <param name="MergeMode">The merge operation used for the member.</param>
    /// <param name="GetValue">Reads the member value from an ordinary model.</param>
    /// <param name="NestedSchemaFactory">Creates the nested fragment schema when the member is structural.</param>
    /// <param name="CollectionMergeStrategy">The custom merge strategy, when configured.</param>
    /// <param name="DefaultValueFactory">Creates the CLR default value for the member.</param>
    /// <param name="CollectionValueFactory">Materializes collection elements into the declared member type.</param>
    public SparseFragmentMemberSchema(
        int Id,
        string Name,
        Type ValueType,
        MergeMode MergeMode,
        Func<object, object?>? GetValue = null,
        Func<SparseFragmentSchema>? NestedSchemaFactory = null,
        ISparseMergeStrategy? CollectionMergeStrategy = null,
        Func<object?>? DefaultValueFactory = null,
        Func<IEnumerable<object?>, object?>? CollectionValueFactory = null
    )
    {
        this.Id = Id;
        this.Name = Name;
        this.ValueType = ValueType;
        this.MergeMode = MergeMode;
        this.GetValue = GetValue;
        this.NestedSchemaFactory = NestedSchemaFactory;
        this.CollectionMergeStrategy = CollectionMergeStrategy;
        this.DefaultValueFactory = DefaultValueFactory;
        this.CollectionValueFactory = CollectionValueFactory;
    }

    /// <summary>The schema-local member ordinal. It may change when the model shape changes.</summary>
    public int Id { get; init; }

    /// <summary>The source member name.</summary>
    public string Name { get; init; }

    /// <summary>The member value type.</summary>
    public Type ValueType { get; init; }

    /// <summary>The merge operation used for the member.</summary>
    public MergeMode MergeMode { get; init; }

    /// <summary>Reads the member value from an ordinary model.</summary>
    public Func<object, object?>? GetValue { get; init; }

    /// <summary>Creates the nested fragment schema when the member is structural.</summary>
    public Func<SparseFragmentSchema>? NestedSchemaFactory { get; init; }

    /// <summary>The custom merge strategy, when configured.</summary>
    public ISparseMergeStrategy? CollectionMergeStrategy { get; init; }

    /// <summary>Creates the CLR default value for the member.</summary>
    public Func<object?>? DefaultValueFactory { get; init; }

    /// <summary>Materializes collection elements into the declared member type.</summary>
    public Func<IEnumerable<object?>, object?>? CollectionValueFactory { get; init; }

    /// <summary>Tests element membership using the declared collection's comparer semantics.</summary>
    public Func<object, object?, bool>? ContainsElement { get; init; }
}

/// <summary>Describes the generated sparse fragment for one model.</summary>
/// <remarks>
/// Advanced tooling vocabulary: describes generated fragment metadata.
/// Member IDs are schema-local ordinals, not persistent identifiers.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class SparseFragmentSchema
{
    private readonly Type _modelType;
    private readonly IReadOnlyList<SparseFragmentMemberSchema> _members;
    private readonly Func<ISparseFragment>? _emptyFragmentFactory;

    /// <summary>Initializes a new instance of the schema.</summary>
    /// <param name="modelType">The ordinary model type.</param>
    /// <param name="members">The member schemas.</param>
    /// <param name="emptyFragmentFactory">Creates an empty fragment instance.</param>
    public SparseFragmentSchema(
        Type modelType,
        IEnumerable<SparseFragmentMemberSchema> members,
        Func<ISparseFragment>? emptyFragmentFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(modelType);
        ArgumentNullException.ThrowIfNull(members);
        _modelType = modelType;
        var memberArray = members.ToArray();
        _members = Array.AsReadOnly(memberArray);
        HasOrdinalMemberIds = CheckOrdinalMemberIds(memberArray);
        _emptyFragmentFactory = emptyFragmentFactory;
    }

    private static bool CheckOrdinalMemberIds(SparseFragmentMemberSchema[] members)
    {
        for (var index = 0; index < members.Length; index++)
        {
            if (members[index].Id != index)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The ordinary model type.</summary>
    public Type ModelType => _modelType;

    /// <summary>The member schemas ordered by schema-local ordinal.</summary>
    /// <remarks>
    /// Generated schemas assign zero-based contiguous ordinals matching this order, so
    /// <c>Members[id].Id == id</c> holds for every generated member ID. Hand-built schemas should
    /// preserve that layout for O(1) lookup; <see cref="TryGetMember"/> still resolves members
    /// correctly when they do not.
    /// </remarks>
    public IReadOnlyList<SparseFragmentMemberSchema> Members => _members;

    /// <summary>
    /// Whether generated member IDs are zero-based contiguous ordinals matching
    /// <see cref="Members"/> order, enabling O(1) indexed lookup.
    /// </summary>
    public bool HasOrdinalMemberIds { get; }

    /// <summary>Resolves a generated member ID to its schema metadata in O(1) for ordinal schemas.</summary>
    /// <param name="memberId">The schema-local generated member ID.</param>
    /// <param name="member">Receives the member metadata when the ID is known.</param>
    /// <returns>Whether the schema contains the generated member ID.</returns>
    /// <remarks>
    /// Ordinal schemas take the indexed fast path. Non-ordinal schemas fall back to a linear scan
    /// so hand-built metadata keeps working; prefer ordinal layout in hot paths.
    /// </remarks>
    public bool TryGetMember(int memberId, out SparseFragmentMemberSchema member)
    {
        var members = _members;
        if ((uint)memberId < (uint)members.Count)
        {
            var candidate = members[memberId];
            if (candidate.Id == memberId)
            {
                member = candidate;
                return true;
            }
        }

        for (var index = 0; index < members.Count; index++)
        {
            if (members[index].Id == memberId)
            {
                member = members[index];
                return true;
            }
        }

        member = default;
        return false;
    }

    /// <summary>Resolves a generated member ID to its schema metadata in O(1) for ordinal schemas.</summary>
    /// <param name="memberId">The schema-local generated member ID.</param>
    /// <returns>The member metadata.</returns>
    /// <exception cref="ArgumentException">The schema has no generated member with the ID.</exception>
    public SparseFragmentMemberSchema GetMember(int memberId)
    {
        if (TryGetMember(memberId, out var member))
        {
            return member;
        }

        throw new ArgumentException(
            $"Schema '{_modelType}' has no generated member with ID {memberId}."
        );
    }

    /// <summary>Creates an empty fragment instance.</summary>
    public ISparseFragment CreateEmptyFragment() =>
        _emptyFragmentFactory?.Invoke()
        ?? throw new InvalidOperationException("This schema has no empty fragment factory.");
}
