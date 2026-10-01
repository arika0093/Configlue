namespace SparseFragments;

/// <summary>Describes one member of a generated sparse fragment schema.</summary>
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
    public SparseFragmentMemberSchema(
        int Id,
        string Name,
        Type ValueType,
        MergeMode MergeMode,
        Func<object, object?>? GetValue = null,
        Func<SparseFragmentSchema>? NestedSchemaFactory = null,
        ISparseMergeStrategy? CollectionMergeStrategy = null,
        Func<object?>? DefaultValueFactory = null
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
}

/// <summary>Describes the generated sparse fragment for one model.</summary>
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
        _members = Array.AsReadOnly(members.ToArray());
        _emptyFragmentFactory = emptyFragmentFactory;
    }

    /// <summary>The ordinary model type.</summary>
    public Type ModelType => _modelType;

    /// <summary>The member schemas ordered by schema-local ordinal.</summary>
    public IReadOnlyList<SparseFragmentMemberSchema> Members => _members;

    /// <summary>Creates an empty fragment instance.</summary>
    public ISparseFragment CreateEmptyFragment() =>
        _emptyFragmentFactory?.Invoke()
        ?? throw new InvalidOperationException("This schema has no empty fragment factory.");
}
