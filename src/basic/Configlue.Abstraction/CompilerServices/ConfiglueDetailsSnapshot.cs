namespace Configlue.CompilerServices;

/// <summary>Per-element provenance transport for one effective collection element.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public readonly record struct ConfigCollectionElementData
{
    /// <summary>Gets or initializes the <see cref="Index"/> value.</summary>
    public int Index { get; init; }

    /// <summary>Gets or initializes the <see cref="Value"/> value.</summary>
    public object? Value { get; init; }

    /// <summary>Gets or initializes the <see cref="SourceIndices"/> value.</summary>
    public IReadOnlyList<int> SourceIndices { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Index">The element index in the effective collection.</param>
    /// <param name="Value">The element value in the effective collection.</param>
    /// <param name="SourceIndices">Indices into the snapshot sources contributing this element, highest priority first.</param>
    public ConfigCollectionElementData(int Index, object? Value, IReadOnlyList<int> SourceIndices)
    {
        this.Index = Index;
        this.Value = Value;
        this.SourceIndices = SourceIndices;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="Index">Receives the current <see cref="Index"/> value.</param>
    /// <param name="Value">Receives the current <see cref="Value"/> value.</param>
    /// <param name="SourceIndices">Receives the current <see cref="SourceIndices"/> value.</param>
    public void Deconstruct(out int Index, out object? Value, out IReadOnlyList<int> SourceIndices)
    {
        Index = this.Index;
        Value = this.Value;
        SourceIndices = this.SourceIndices;
    }
}

/// <summary>One internally consistent resolution snapshot backing generated configuration details.</summary>
/// <remarks>Created by the state runtime; consumed by generated details trees without further source reads.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class ConfiglueDetailsSnapshot
{
    internal ConfiglueDetailsSnapshot(
        ConfiglueModelSchema schema,
        object? value,
        IReadOnlyList<ConfigSourceDetails> sources,
        IReadOnlyList<IConfiglueFragment?> sourceFragments,
        IReadOnlyList<StateReadStatus> sourceStatuses,
        Func<ConfiglueMemberPath, ConfiglueEditability> editability,
        Func<ConfiglueMemberPath, IReadOnlyList<ConfigCollectionElementData>> collectionElements
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sourceFragments);
        ArgumentNullException.ThrowIfNull(sourceStatuses);
        ArgumentNullException.ThrowIfNull(editability);
        ArgumentNullException.ThrowIfNull(collectionElements);
        Schema = schema;
        Value = value;
        Sources = sources;
        SourceFragments = sourceFragments;
        SourceStatuses = sourceStatuses;
        Editability = editability;
        CollectionElements = collectionElements;
    }

    /// <summary>The resolved root model schema.</summary>
    public ConfiglueModelSchema Schema { get; }

    /// <summary>The resolved root model value.</summary>
    public object? Value { get; }

    /// <summary>Source descriptors, ordered from highest to lowest priority.</summary>
    public IReadOnlyList<ConfigSourceDetails> Sources { get; }

    /// <summary>Per-source contribution fragments aligned with <see cref="Sources"/>; null when a source contributes nothing.</summary>
    public IReadOnlyList<IConfiglueFragment?> SourceFragments { get; }

    /// <summary>Per-source read statuses aligned with <see cref="Sources"/>.</summary>
    public IReadOnlyList<StateReadStatus> SourceStatuses { get; }

    /// <summary>Resolves editability for a generated model member path.</summary>
    public Func<ConfiglueMemberPath, ConfiglueEditability> Editability { get; }

    /// <summary>Resolves per-element provenance for a generated collection member path.</summary>
    public Func<
        ConfiglueMemberPath,
        IReadOnlyList<ConfigCollectionElementData>
    > CollectionElements { get; }
}
