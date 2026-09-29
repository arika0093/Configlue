using Amazon.S3;

namespace Configlue.Resource.S3;

/// <summary>Options for a resource backed by one Amazon S3 object.</summary>
public sealed class S3ObjectResourceOptions
{
    /// <summary>An optional stable identity overriding the identity derived from bucket and key.</summary>
    public ResourceId? ResourceId { get; init; }

    /// <summary>Resolves the bucket for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? BucketNameSelector { get; init; }

    /// <summary>Resolves the object key for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string>? KeySelector { get; init; }

    /// <summary>Resolves an externally owned S3 client for each subject-aware operation.</summary>
    /// <remarks>Use a distinct route when the selected client points to a different S3 account or region.</remarks>
    public Func<ConfiglueResourceContext, IAmazonS3>? ClientSelector { get; init; }
}
