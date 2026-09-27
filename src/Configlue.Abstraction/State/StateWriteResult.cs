namespace Configlue;

/// <summary>The result of writing state.</summary>
public readonly record struct StateWriteResult
{
    /// <summary>Gets or initializes the <see cref="Revision"/> value.</summary>
    public string? Revision { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Revision">The initial value for the <see cref="Revision"/> property.</param>
    public StateWriteResult(string? Revision)
    {
        this.Revision = Revision;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="Revision">Receives the current <see cref="Revision"/> value.</param>
    public void Deconstruct(out string? Revision)
    {
        Revision = this.Revision;
    }

    /// <summary>Per-source outcomes when a configure session wrote to multiple sources.</summary>
    public StateMultiWriteResult? MultiWriteResult { get; init; }
}
