using Amazon.S3;

namespace Configlue.Resource.S3;

/// <summary>Options for a resource backed by one Amazon S3 object.</summary>
public sealed class S3ObjectResourceOptions
{
    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected buckets and keys share one physical coordination domain; an
    /// incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>Resolves the bucket for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? BucketNameSelector { get; init; }

    /// <summary>Resolves the object key for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string>? KeySelector { get; init; }

    /// <summary>Resolves an externally owned S3 client for each subject-aware operation.</summary>
    /// <remarks>Use a distinct route when the selected client points to a different S3 account or region.</remarks>
    public Func<ConfiglueResourceContext, IAmazonS3>? ClientSelector { get; init; }
}
