namespace Configlue.CompilerServices;

/// <summary>One internally consistent resolution snapshot backing generated configuration details.</summary>
/// <remarks>
/// Created by the state runtime; consumed by generated details trees without further source reads.
/// Carries member-level provenance only (effective source, per-source states, editability).
/// Per-element collection provenance is opt-in via the advanced
/// <see cref="Configlue.ConfiglueMergeProvenance"/> explanation API and is not part of this snapshot.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class ConfiglueDetailsSnapshot
{
    internal ConfiglueDetailsSnapshot(
        ConfiglueModelSchema schema,
        object? value,
        IReadOnlyList<ConfigSourceDetails> sources,
        IReadOnlyList<IConfiglueFragment?> sourceFragments,
        IReadOnlyList<StateReadStatus> sourceStatuses,
        Func<ConfiglueMemberPath, ConfiglueEditability> editability
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sourceFragments);
        ArgumentNullException.ThrowIfNull(sourceStatuses);
        ArgumentNullException.ThrowIfNull(editability);
        Schema = schema;
        Value = value;
        Sources = sources;
        SourceFragments = sourceFragments;
        SourceStatuses = sourceStatuses;
        Editability = editability;
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
}
