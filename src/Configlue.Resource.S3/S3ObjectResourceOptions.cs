namespace Configlue.Resource.S3;

/// <summary>Options for a resource backed by one Amazon S3 object.</summary>
public sealed class S3ObjectResourceOptions
{
    /// <summary>An optional stable identity overriding the identity derived from bucket and key.</summary>
    public ResourceId? ResourceId { get; init; }
}
