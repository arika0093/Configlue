namespace Configlue;

/// <summary>A source contribution supplied to a custom merge strategy.</summary>
public readonly record struct ConfiglueMergeSourceValue
{
    /// <summary>Gets or initializes the <see cref="SourceId"/> value.</summary>
    public SourceId SourceId { get; init; }

    /// <summary>Gets or initializes the <see cref="Value"/> value.</summary>
    public Optional<object?> Value { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="SourceId">The initial value for the <see cref="SourceId"/> property.</param>
    /// <param name="Value">The initial value for the <see cref="Value"/> property.</param>
    public ConfiglueMergeSourceValue(SourceId SourceId, Optional<object?> Value)
    {
        this.SourceId = SourceId;
        this.Value = Value;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="SourceId">Receives the current <see cref="SourceId"/> value.</param>
    /// <param name="Value">Receives the current <see cref="Value"/> value.</param>
    public void Deconstruct(out SourceId SourceId, out Optional<object?> Value)
    {
        SourceId = this.SourceId;
        Value = this.Value;
    }
}

/// <summary>A source contribution supplied to a typed custom merge strategy.</summary>
public readonly record struct ConfiglueMergeSourceValue<T>
{
    /// <summary>Gets or initializes the <see cref="SourceId"/> value.</summary>
    public SourceId SourceId { get; init; }

    /// <summary>Gets or initializes the <see cref="Value"/> value.</summary>
    public Optional<T> Value { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="SourceId">The initial value for the <see cref="SourceId"/> property.</param>
    /// <param name="Value">The initial value for the <see cref="Value"/> property.</param>
    public ConfiglueMergeSourceValue(SourceId SourceId, Optional<T> Value)
    {
        this.SourceId = SourceId;
        this.Value = Value;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="SourceId">Receives the current <see cref="SourceId"/> value.</param>
    /// <param name="Value">Receives the current <see cref="Value"/> value.</param>
    public void Deconstruct(out SourceId SourceId, out Optional<T> Value)
    {
        SourceId = this.SourceId;
        Value = this.Value;
    }
}

/// <summary>Identifies the sources that contributed an effective collection element.</summary>
public sealed class ConfiglueMergeElementProvenance
{
    /// <summary>Creates element provenance.</summary>
    public ConfiglueMergeElementProvenance(int index, IEnumerable<SourceId> sourceIds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(sourceIds);
        Index = index;
        SourceIds = Array.AsReadOnly(sourceIds.ToArray());
    }

    /// <summary>The effective collection index.</summary>
    public int Index { get; }

    /// <summary>Logical source IDs that contributed this element.</summary>
    public IReadOnlyList<SourceId> SourceIds { get; }
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
}

/// <summary>Optional edit-rebase behavior for a custom merge strategy.</summary>
public interface IConfiglueMergeRebaseStrategy
{
    /// <summary>Reapplies an edit to a newer value, or returns a reason that it cannot be rebased.</summary>
    bool TryRebaseObject(
        object? editBase,
        object? desired,
        object? current,
        out object? rebased,
        out string? reason
    );
}

/// <summary>Optional source-local contribution planning for a custom merge strategy.</summary>
public interface IConfiglueMergeContributionPlanner
{
    /// <summary>Plans one source's contribution to realize the requested effective value.</summary>
    bool TryPlanSourceContributionObject(
        IReadOnlyList<ConfiglueMergeSourceValue> sourceValuesLowToHigh,
        SourceId targetSourceId,
        object? desiredEffective,
        out Optional<object?> targetContribution,
        out string? reason
    );
}

/// <summary>Optional element provenance for a custom merge strategy.</summary>
public interface IConfiglueMergeElementProvenanceProvider
{
    /// <summary>Maps effective collection elements to their contributing source IDs.</summary>
    IReadOnlyList<ConfiglueMergeElementProvenance> ExplainElementsObject(
        object? effective,
        IReadOnlyList<ConfiglueMergeSourceValue> sourceValuesLowToHigh
    );
}

/// <summary>
/// Implements the required merge and equality algebra for a member. Optional capabilities can be implemented
/// independently through <see cref="IConfiglueMergeRebaseStrategy"/>,
/// <see cref="IConfiglueMergeContributionPlanner"/>, and
/// <see cref="IConfiglueMergeElementProvenanceProvider"/>.
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
    public virtual bool TryRebase(
        T? editBase,
        T? desired,
        T? current,
        out T? rebased,
        out string? reason
    )
    {
        rebased = default;
        reason = "This merge strategy does not support edit rebasing.";
        return false;
    }

    /// <summary>Plans the target source contribution needed to produce a requested effective value.</summary>
    public virtual bool TryPlanSourceContribution(
        IReadOnlyList<ConfiglueMergeSourceValue<T>> sourceValuesLowToHigh,
        SourceId targetSourceId,
        T? desiredEffective,
        out Optional<T> targetContribution,
        out string? reason
    )
    {
        targetContribution = default;
        reason = "This merge strategy does not support source contribution planning.";
        return false;
    }

    /// <summary>Maps effective collection elements to the source IDs that contributed each element.</summary>
    /// <remarks>Return one entry per effective element. Source IDs must refer to supplied contributions.</remarks>
    public virtual IReadOnlyList<ConfiglueMergeElementProvenance> ExplainElements(
        T? effective,
        IReadOnlyList<ConfiglueMergeSourceValue<T>> sourceValuesLowToHigh
    ) => Array.Empty<ConfiglueMergeElementProvenance>();

    Type IConfiglueMergeStrategy.ValueType => typeof(T);

    Optional<object?> IConfiglueMergeStrategy.Merge(
        Optional<object?> lowerPriority,
        Optional<object?> higherPriority
    ) => Box(Merge(Unbox(lowerPriority), Unbox(higherPriority)));

    bool IConfiglueMergeStrategy.AreEqual(object? left, object? right) =>
        AreEqual((T?)left, (T?)right);

    /// <summary>Untyped adapter used by the optional rebase capability.</summary>
    public bool TryRebaseObject(
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

    /// <summary>Untyped adapter used by the optional contribution planning capability.</summary>
    public bool TryPlanSourceContributionObject(
        IReadOnlyList<ConfiglueMergeSourceValue> sourceValuesLowToHigh,
        SourceId targetSourceId,
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

    /// <summary>Untyped adapter used by the optional element provenance capability.</summary>
    public IReadOnlyList<ConfiglueMergeElementProvenance> ExplainElementsObject(
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
