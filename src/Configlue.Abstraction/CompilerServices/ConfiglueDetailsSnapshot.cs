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
/// <remarks>Created by the options runtime; consumed by generated details trees without further source reads.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class ConfiglueDetailsSnapshot
{
    internal ConfiglueDetailsSnapshot(
        ConfiglueModelSchema schema,
        object? value,
        IReadOnlyList<ConfigSourceDetails> sources,
        IReadOnlyList<IConfiglueFragment?> sourceFragments,
        IReadOnlyList<StateReadStatus> sourceStatuses,
        Func<string, ConfiglueEditability> editability,
        Func<string, IReadOnlyList<ConfigCollectionElementData>> collectionElements
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

    /// <summary>Resolves editability for a dotted model member path.</summary>
    public Func<string, ConfiglueEditability> Editability { get; }

    /// <summary>Resolves per-element provenance for a dotted collection member path.</summary>
    public Func<string, IReadOnlyList<ConfigCollectionElementData>> CollectionElements { get; }

    /// <summary>Navigates a sparse fragment by member name segments.</summary>
    public static bool TryGetPathValue(
        IConfiglueFragment? fragment,
        string[] segments,
        out object? value
    )
    {
        ArgumentNullException.ThrowIfNull(segments);
        value = null;
        var current = fragment;
        for (var index = 0; index < segments.Length; index++)
        {
            if (current is null)
            {
                return false;
            }

            var match = current
                .EnumeratePresentMembers()
                .Where(member =>
                    string.Equals(member.Name, segments[index], StringComparison.Ordinal)
                )
                .Cast<ConfiglueFragmentMember?>()
                .FirstOrDefault();
            if (!match.HasValue)
            {
                return false;
            }

            var present = match.Value;

            if (index == segments.Length - 1)
            {
                value = present.Value;
                return true;
            }

            current = present.Value as IConfiglueFragment;
        }

        return false;
    }
}
