namespace Configlue;

/// <summary>The bytes to persist and the revision on which the write is based.</summary>
public readonly record struct ResourceWriteRequest
{
    /// <summary>Gets or initializes the <see cref="Content"/> value.</summary>
    public ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>Gets or initializes the <see cref="ExpectedRevision"/> value.</summary>
    public string? ExpectedRevision { get; init; }

    /// <summary>Gets or initializes the <see cref="Schema"/> value.</summary>
    public StateSchemaMetadata? Schema { get; init; }

    /// <summary>Gets or initializes the <see cref="CheckRevision"/> value.</summary>
    public bool CheckRevision { get; init; }

    internal bool ContentIsOwned { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Content">The initial value for the <see cref="Content"/> property.</param>
    /// <param name="ExpectedRevision">The initial value for the <see cref="ExpectedRevision"/> property.</param>
    /// <param name="Schema">The initial value for the <see cref="Schema"/> property.</param>
    /// <param name="CheckRevision">The initial value for the <see cref="CheckRevision"/> property.</param>
    public ResourceWriteRequest(
        ReadOnlyMemory<byte> Content,
        string? ExpectedRevision = null,
        StateSchemaMetadata? Schema = null,
        bool CheckRevision = false
    )
    {
        this.Content = Content;
        this.ExpectedRevision = ExpectedRevision;
        this.Schema = Schema;
        this.CheckRevision = CheckRevision;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="Content">Receives the current <see cref="Content"/> value.</param>
    /// <param name="ExpectedRevision">Receives the current <see cref="ExpectedRevision"/> value.</param>
    /// <param name="Schema">Receives the current <see cref="Schema"/> value.</param>
    /// <param name="CheckRevision">Receives the current <see cref="CheckRevision"/> value.</param>
    public void Deconstruct(
        out ReadOnlyMemory<byte> Content,
        out string? ExpectedRevision,
        out StateSchemaMetadata? Schema,
        out bool CheckRevision
    )
    {
        Content = this.Content;
        ExpectedRevision = this.ExpectedRevision;
        Schema = this.Schema;
        CheckRevision = this.CheckRevision;
    }
}
