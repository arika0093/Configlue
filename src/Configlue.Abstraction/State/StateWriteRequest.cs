namespace Configlue.State;

/// <summary>The state value to persist with an explicit revision precondition.</summary>
public readonly record struct StateWriteRequest<T>
{
    /// <summary>The value to persist.</summary>
    public T Value { get; init; }

    /// <summary>The concurrency precondition. The default is an unchecked write.</summary>
    public RevisionCondition Condition { get; init; }

    /// <summary>Creates a state write request.</summary>
    public StateWriteRequest(T Value, RevisionCondition Condition = default)
    {
        this.Value = Value;
        this.Condition = Condition;
    }

    /// <summary>Deconstructs the value and revision condition.</summary>
    public void Deconstruct(out T Value, out RevisionCondition Condition)
    {
        Value = this.Value;
        Condition = this.Condition;
    }
}
