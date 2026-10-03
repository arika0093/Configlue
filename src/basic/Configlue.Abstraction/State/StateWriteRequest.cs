namespace Configlue.State;

/// <summary>The state value to persist with an explicit revision precondition.</summary>
public sealed record StateWriteRequest<T>
{
    /// <summary>The value to persist.</summary>
    public T Value { get; }

    /// <summary>The concurrency precondition. The default is an unchecked write.</summary>
    public RevisionCondition Condition { get; }

    /// <summary>Creates a state write request.</summary>
    public StateWriteRequest(T Value, RevisionCondition Condition = default)
    {
        if ((object?)Value is null)
        {
            throw new ArgumentNullException(nameof(Value), "A state write value cannot be null.");
        }
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
