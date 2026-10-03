namespace Configlue.Resources;

/// <summary>The resource content to persist with an explicit revision precondition.</summary>
public readonly record struct ResourceWriteRequest
{
    /// <summary>The content to persist.</summary>
    public ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>The concurrency precondition. The default is an unchecked write.</summary>
    public RevisionCondition Condition { get; init; }

    /// <summary>The persisted state schema, when available.</summary>
    public StateSchemaMetadata? Schema { get; init; }

    internal bool ContentIsOwned { get; init; }

    internal IDisposable? ContentOwner { get; init; }

    /// <summary>Creates a resource write request.</summary>
    public ResourceWriteRequest(
        ReadOnlyMemory<byte> Content,
        RevisionCondition Condition = default,
        StateSchemaMetadata? Schema = null
    )
    {
        if (Schema is { IsValid: false })
        {
            throw new ArgumentException(
                "Resource schema metadata must have a positive schema version.",
                nameof(Schema)
            );
        }
        this.Content = Content;
        this.Condition = Condition;
        this.Schema = Schema;
    }

    /// <summary>Deconstructs the content, concurrency condition, and schema.</summary>
    public void Deconstruct(
        out ReadOnlyMemory<byte> Content,
        out RevisionCondition Condition,
        out StateSchemaMetadata? Schema
    )
    {
        Content = this.Content;
        Condition = this.Condition;
        Schema = this.Schema;
    }
}
