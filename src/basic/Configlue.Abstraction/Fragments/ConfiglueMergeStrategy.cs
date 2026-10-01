namespace Configlue;

/// <summary>A source contribution supplied to a custom Configlue merge strategy.</summary>
public readonly record struct ConfiglueMergeSourceValue(
    string SourceId,
    Optional<object?> Value
);

/// <summary>A source contribution supplied to a typed custom Configlue merge strategy.</summary>
public readonly record struct ConfiglueMergeSourceValue<T>(
    string SourceId,
    Optional<T> Value
);

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

/// <summary>
/// Extends the generic sparse merge algebra with Configlue-specific rebase, write-planning, and provenance operations.
/// </summary>
public interface IConfiglueMergeStrategy : ISparseMergeStrategy
{
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
/// Extends <see cref="FragmentMergeStrategy{T}"/> with the operations required by Configlue's source-aware write algebra.
/// </summary>
/// <typeparam name="T">The model member type.</typeparam>
public abstract class ConfiglueMergeStrategy<T> : FragmentMergeStrategy<T>, IConfiglueMergeStrategy
{
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
    public abstract IReadOnlyList<ConfiglueMergeElementProvenance> ExplainElements(
        T? effective,
        IReadOnlyList<ConfiglueMergeSourceValue<T>> sourceValuesLowToHigh
    );

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
