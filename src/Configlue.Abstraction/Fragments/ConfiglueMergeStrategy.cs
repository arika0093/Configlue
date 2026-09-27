namespace Configlue;

/// <summary>A source contribution supplied to a custom merge strategy.</summary>
public readonly record struct ConfiglueMergeSourceValue(string SourceId, Optional<object?> Value);

/// <summary>A source contribution supplied to a typed custom merge strategy.</summary>
public readonly record struct ConfiglueMergeSourceValue<T>(string SourceId, Optional<T> Value);

/// <summary>Identifies the sources that contributed an effective collection element.</summary>
public sealed class ConfiglueMergeElementProvenance
{
    /// <summary>Creates element provenance.</summary>
    public ConfiglueMergeElementProvenance(int index, IEnumerable<string> sourceIds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(sourceIds);
        Index = index;
        SourceIds = Array.AsReadOnly(sourceIds.ToArray());
    }

    /// <summary>The effective collection index.</summary>
    public int Index { get; }

    /// <summary>Logical source IDs that contributed this element.</summary>
    public IReadOnlyList<string> SourceIds { get; }
}

/// <summary>Untyped operations used by generated metadata and the runtime.</summary>
public interface IConfiglueMergeStrategy
{
    /// <summary>The member value type handled by this strategy.</summary>
    Type ValueType { get; }

    /// <summary>Merges lower and higher priority values, including missing and null values.</summary>
    Optional<object?> Merge(Optional<object?> lowerPriority, Optional<object?> higherPriority);

    /// <summary>Compares two member values using the strategy's semantic equality.</summary>
    bool AreEqual(object? left, object? right);

    /// <summary>Reapplies an edit to a newer value, or returns a reason that it cannot be rebased.</summary>
    bool TryRebase(
        object? editBase,
        object? desired,
        object? current,
        out object? rebased,
        out string? reason
    );

    /// <summary>Plans one source's contribution to realize the requested effective value.</summary>
    bool TryPlanSourceContribution(
        IReadOnlyList<ConfiglueMergeSourceValue> sourceValuesLowToHigh,
        string targetSourceId,
        object? desiredEffective,
        out Optional<object?> targetContribution,
        out string? reason
    );

    /// <summary>Maps effective collection elements to their contributing source IDs.</summary>
    IReadOnlyList<ConfiglueMergeElementProvenance> ExplainElements(
        object? effective,
        IReadOnlyList<ConfiglueMergeSourceValue> sourceValuesLowToHigh
    );
}

/// <summary>
/// Implements every operation required for a member-specific merge algebra. Source values are ordered from
/// lowest to highest priority; callers receive a reason when a rebase or source-local edit is not representable.
/// </summary>
/// <remarks>
/// Generated models keep one strategy instance and may call it concurrently. Implementations must be stateless or
/// thread-safe.
/// </remarks>
/// <typeparam name="T">The model member type.</typeparam>
public abstract class ConfiglueMergeStrategy<T> : IConfiglueMergeStrategy
{
    /// <summary>Merges two presence-aware member values.</summary>
    public abstract Optional<T> Merge(Optional<T> lowerPriority, Optional<T> higherPriority);

    /// <summary>Compares two presence-aware member values according to this algebra.</summary>
    public abstract bool AreEqual(T? left, T? right);

    /// <summary>Reapplies an edit based on an earlier value to the current value.</summary>
    public abstract bool TryRebase(
        T? editBase,
        T? desired,
        T? current,
        out T? rebased,
        out string? reason
    );

    /// <summary>Plans the target source contribution needed to produce a requested effective value.</summary>
    public abstract bool TryPlanSourceContribution(
        IReadOnlyList<ConfiglueMergeSourceValue<T>> sourceValuesLowToHigh,
        string targetSourceId,
        T? desiredEffective,
        out Optional<T> targetContribution,
        out string? reason
    );

    /// <summary>Maps effective collection elements to the source IDs that contributed each element.</summary>
    /// <remarks>Return one entry per effective element. Source IDs must refer to supplied contributions.</remarks>
    public abstract IReadOnlyList<ConfiglueMergeElementProvenance> ExplainElements(
        T? effective,
        IReadOnlyList<ConfiglueMergeSourceValue<T>> sourceValuesLowToHigh
    );

    Type IConfiglueMergeStrategy.ValueType => typeof(T);

    Optional<object?> IConfiglueMergeStrategy.Merge(
        Optional<object?> lowerPriority,
        Optional<object?> higherPriority
    ) => Box(Merge(Unbox(lowerPriority), Unbox(higherPriority)));

    bool IConfiglueMergeStrategy.AreEqual(object? left, object? right) =>
        AreEqual((T?)left, (T?)right);

    bool IConfiglueMergeStrategy.TryRebase(
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

    bool IConfiglueMergeStrategy.TryPlanSourceContribution(
        IReadOnlyList<ConfiglueMergeSourceValue> sourceValuesLowToHigh,
        string targetSourceId,
        object? desiredEffective,
        out Optional<object?> targetContribution,
        out string? reason
    )
    {
        ArgumentNullException.ThrowIfNull(sourceValuesLowToHigh);
        var typedValues = sourceValuesLowToHigh
            .Select(static source => new ConfiglueMergeSourceValue<T>(
                source.SourceId,
                Unbox(source.Value)
            ))
            .ToArray();
        var succeeded = TryPlanSourceContribution(
            typedValues,
            targetSourceId,
            (T?)desiredEffective,
            out var typedContribution,
            out reason
        );
        targetContribution = Box(typedContribution);
        return succeeded;
    }

    IReadOnlyList<ConfiglueMergeElementProvenance> IConfiglueMergeStrategy.ExplainElements(
        object? effective,
        IReadOnlyList<ConfiglueMergeSourceValue> sourceValuesLowToHigh
    )
    {
        ArgumentNullException.ThrowIfNull(sourceValuesLowToHigh);
        var typedValues = sourceValuesLowToHigh
            .Select(static source => new ConfiglueMergeSourceValue<T>(
                source.SourceId,
                Unbox(source.Value)
            ))
            .ToArray();
        return ExplainElements((T?)effective, typedValues)
            ?? throw new InvalidOperationException(
                "A merge strategy returned null element provenance."
            );
    }

    private static Optional<T> Unbox(Optional<object?> value) =>
        value.IsPresent ? Optional<T>.Present((T?)value.Value) : Optional<T>.Missing;

    private static Optional<object?> Box(Optional<T> value) =>
        value.IsPresent ? Optional<object?>.Present(value.Value) : Optional<object?>.Missing;
}
