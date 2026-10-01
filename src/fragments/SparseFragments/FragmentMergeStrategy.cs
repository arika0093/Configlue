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

    Type ISparseMergeStrategy.ValueType => typeof(T);

    Optional<object?> ISparseMergeStrategy.Merge(
        Optional<object?> lowerPriority,
        Optional<object?> higherPriority
    ) => Box(Merge(Unbox(lowerPriority), Unbox(higherPriority)));

    bool ISparseMergeStrategy.AreEqual(object? left, object? right) =>
        AreEqual((T?)left, (T?)right);

    private static Optional<T> Unbox(Optional<object?> value) =>
        value.IsPresent ? Optional<T>.Present((T?)value.Value) : Optional<T>.Missing;

    private static Optional<object?> Box(Optional<T> value) =>
        value.IsPresent ? Optional<object?>.Present(value.Value) : Optional<object?>.Missing;
}
