namespace SparseFragments;

/// <summary>Untyped member merge and equality operations used by generated metadata.</summary>
public interface ISparseMergeStrategy
{
    /// <summary>The member value type handled by this strategy.</summary>
    Type ValueType { get; }

    /// <summary>Merges lower and higher priority values, including missing and null values.</summary>
    Optional<object?> Merge(Optional<object?> lowerPriority, Optional<object?> higherPriority);

    /// <summary>Compares two member values using the strategy's semantic equality.</summary>
    bool AreEqual(object? left, object? right);

    /// <summary>
    /// Reapplies an edit made against <paramref name="editBase"/> onto <paramref name="current"/>, or returns a reason
    /// when the concurrent change cannot be reconciled.
    /// </summary>
    bool TryRebase(
        object? editBase,
        object? desired,
        object? current,
        out object? rebased,
        out string? reason
    );

    /// <summary>
    /// Solves for the contribution of one priority index that realizes <paramref name="desiredEffective"/> given the
    /// other contributions, or returns a reason when the target cannot be planned.
    /// </summary>
    bool TryPlanContribution(
        IReadOnlyList<SparseContribution> contributionsLowToHigh,
        int targetContributionIndex,
        object? desiredEffective,
        out Optional<object?> targetContribution,
        out string? reason
    );

    /// <summary>Maps effective collection elements to the contribution indices that produced each element.</summary>
    IReadOnlyList<SparseMergeElementProvenance> ExplainElements(
        object? effective,
        IReadOnlyList<SparseContribution> contributionsLowToHigh
    );
}

/// <summary>
/// Implements the generic member-specific merge algebra shared by sparse fragments. Source values are merged from
/// lowest to highest priority.
/// </summary>
/// <remarks>
/// Generated models keep one strategy instance and may call it concurrently. Implementations must be stateless or
/// thread-safe.
/// </remarks>
/// <typeparam name="T">The model member type.</typeparam>
public abstract class FragmentMergeStrategy<T> : ISparseMergeStrategy
{
    /// <summary>Merges two presence-aware member values.</summary>
    public abstract Optional<T> Merge(Optional<T> lowerPriority, Optional<T> higherPriority);

    /// <summary>Compares two presence-aware member values according to this algebra.</summary>
    public abstract bool AreEqual(T? left, T? right);

    /// <summary>
    /// Reapplies an edit based on an earlier value to the current value. The default implementation treats the edit as
    /// rebaseable whenever one of the three values is shared, and reports a conflict otherwise.
    /// </summary>
    public virtual bool TryRebase(
        T? editBase,
        T? desired,
        T? current,
        out T? rebased,
        out string? reason
    )
    {
        if (AreEqual(desired, editBase))
        {
            rebased = current;
            reason = null;
            return true;
        }
        if (AreEqual(current, editBase) || AreEqual(current, desired))
        {
            rebased = desired;
            reason = null;
            return true;
        }

        rebased = default;
        reason = "The value conflicts with a concurrent change.";
        return false;
    }

    /// <summary>
    /// Plans the target contribution needed to produce a requested effective value. The default implementation only
    /// plans the highest-priority contribution and leaves more general planning to derived strategies.
    /// </summary>
    public virtual bool TryPlanContribution(
        IReadOnlyList<SparseContribution<T>> contributionsLowToHigh,
        int targetContributionIndex,
        T? desiredEffective,
        out Optional<T> targetContribution,
        out string? reason
    )
    {
        ArgumentNullException.ThrowIfNull(contributionsLowToHigh);
        if (
            targetContributionIndex < 0
            || targetContributionIndex >= contributionsLowToHigh.Count
            || targetContributionIndex != contributionsLowToHigh.Count - 1
        )
        {
            targetContribution = Optional<T>.Missing;
            reason = "The default strategy only plans the highest-priority contribution.";
            return false;
        }

        targetContribution = Optional<T>.Present(desiredEffective);
        reason = null;
        return true;
    }

    /// <summary>Maps effective collection elements to their contributing indices. The default returns no provenance.</summary>
    public virtual IReadOnlyList<SparseMergeElementProvenance> ExplainElements(
        T? effective,
        IReadOnlyList<SparseContribution<T>> contributionsLowToHigh
    )
    {
        ArgumentNullException.ThrowIfNull(contributionsLowToHigh);
        return [];
    }

    Type ISparseMergeStrategy.ValueType => typeof(T);

    Optional<object?> ISparseMergeStrategy.Merge(
        Optional<object?> lowerPriority,
        Optional<object?> higherPriority
    ) => Box(Merge(Unbox(lowerPriority), Unbox(higherPriority)));

    bool ISparseMergeStrategy.AreEqual(object? left, object? right) =>
        AreEqual((T?)left, (T?)right);

    bool ISparseMergeStrategy.TryRebase(
        object? editBase,
        object? desired,
        object? current,
        out object? rebased,
        out string? reason
    )
    {
        var succeeded = TryRebase(
            (T?)editBase,
            (T?)desired,
            (T?)current,
            out var typedRebased,
            out reason
        );
        rebased = typedRebased;
        return succeeded;
    }

    bool ISparseMergeStrategy.TryPlanContribution(
        IReadOnlyList<SparseContribution> contributionsLowToHigh,
        int targetContributionIndex,
        object? desiredEffective,
        out Optional<object?> targetContribution,
        out string? reason
    )
    {
        ArgumentNullException.ThrowIfNull(contributionsLowToHigh);
        var typed = contributionsLowToHigh
            .Select(static contribution => new SparseContribution<T>(
                contribution.Index,
                Unbox(contribution.Value)
            ))
            .ToArray();
        var succeeded = TryPlanContribution(
            typed,
            targetContributionIndex,
            (T?)desiredEffective,
            out var typedContribution,
            out reason
        );
        targetContribution = Box(typedContribution);
        return succeeded;
    }

    IReadOnlyList<SparseMergeElementProvenance> ISparseMergeStrategy.ExplainElements(
        object? effective,
        IReadOnlyList<SparseContribution> contributionsLowToHigh
    )
    {
        ArgumentNullException.ThrowIfNull(contributionsLowToHigh);
        var typed = contributionsLowToHigh
            .Select(static contribution => new SparseContribution<T>(
                contribution.Index,
                Unbox(contribution.Value)
            ))
            .ToArray();
        return ExplainElements((T?)effective, typed)
            ?? throw new InvalidOperationException(
                "A merge strategy returned null element provenance."
            );
    }

    private static Optional<T> Unbox(Optional<object?> value) =>
        value.IsPresent ? Optional<T>.Present((T?)value.Value) : Optional<T>.Missing;

    private static Optional<object?> Box(Optional<T> value) =>
        value.IsPresent ? Optional<object?>.Present(value.Value) : Optional<object?>.Missing;
}
