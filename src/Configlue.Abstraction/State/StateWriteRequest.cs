namespace Configlue;

/// <summary>A state value and the revision on which the write is based.</summary>
public readonly record struct StateWriteRequest<T>
{
    /// <summary>Gets or initializes the <see cref="Value"/> value.</summary>
    public T Value { get; init; }

    /// <summary>Gets or initializes the <see cref="ExpectedRevision"/> value.</summary>
    public string? ExpectedRevision { get; init; }

    /// <summary>Gets or initializes the <see cref="CheckRevision"/> value.</summary>
    public bool CheckRevision { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Value">The initial value for the <see cref="Value"/> property.</param>
    /// <param name="ExpectedRevision">The initial value for the <see cref="ExpectedRevision"/> property.</param>
    /// <param name="CheckRevision">The initial value for the <see cref="CheckRevision"/> property.</param>
    public StateWriteRequest(T Value, string? ExpectedRevision = null, bool CheckRevision = false)
    {
        this.Value = Value;
        this.ExpectedRevision = ExpectedRevision;
        this.CheckRevision = CheckRevision;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="Value">Receives the current <see cref="Value"/> value.</param>
    /// <param name="ExpectedRevision">Receives the current <see cref="ExpectedRevision"/> value.</param>
    /// <param name="CheckRevision">Receives the current <see cref="CheckRevision"/> value.</param>
    public void Deconstruct(out T Value, out string? ExpectedRevision, out bool CheckRevision)
    {
        Value = this.Value;
        ExpectedRevision = this.ExpectedRevision;
        CheckRevision = this.CheckRevision;
    }
}
