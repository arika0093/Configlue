namespace SparseFragments;

/// <summary>Identifies one source contribution by its priority index and presence-aware value.</summary>
/// <remarks>
/// A contribution index is a purely positional identity ordered from lowest to highest priority. It deliberately
/// carries no source, resource or revision metadata so the standalone algebra stays domain-neutral.
/// </remarks>
public readonly record struct SparseContribution
{
    /// <summary>Initializes a new contribution.</summary>
    /// <param name="Index">The zero-based priority index ordered from lowest to highest.</param>
    /// <param name="Value">The presence-aware contribution value.</param>
    public SparseContribution(int Index, Optional<object?> Value)
    {
        this.Index = Index;
        this.Value = Value;
    }

    /// <summary>The zero-based priority index ordered from lowest to highest priority.</summary>
    public int Index { get; init; }

    /// <summary>The presence-aware contribution value.</summary>
    public Optional<object?> Value { get; init; }
}

/// <summary>A typed source contribution identified by its priority index.</summary>
/// <typeparam name="T">The contribution value type.</typeparam>
public readonly record struct SparseContribution<T>
{
    /// <summary>Initializes a new typed contribution.</summary>
    /// <param name="Index">The zero-based priority index ordered from lowest to highest.</param>
    /// <param name="Value">The presence-aware contribution value.</param>
    public SparseContribution(int Index, Optional<T> Value)
    {
        this.Index = Index;
        this.Value = Value;
    }

    /// <summary>The zero-based priority index ordered from lowest to highest priority.</summary>
    public int Index { get; init; }

    /// <summary>The presence-aware contribution value.</summary>
    public Optional<T> Value { get; init; }
}

/// <summary>Identifies the contributions that determined one effective collection element.</summary>
public sealed class SparseMergeElementProvenance
{
    /// <summary>Initializes element provenance.</summary>
    /// <param name="index">The effective collection index.</param>
    /// <param name="contributionIndices">The contribution indices that supplied or voted the element.</param>
    public SparseMergeElementProvenance(int index, IEnumerable<int> contributionIndices)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(contributionIndices);
        Index = index;
        ContributionIndices = Array.AsReadOnly(contributionIndices.ToArray());
    }

    /// <summary>The effective collection index.</summary>
    public int Index { get; }

    /// <summary>The contribution indices that contributed this element.</summary>
    public IReadOnlyList<int> ContributionIndices { get; }
}
