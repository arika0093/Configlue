using Google.Cloud.Storage.V1;

namespace Configlue.Resource.Gcs;

/// <summary>Options for a resource backed by one Google Cloud Storage object.</summary>
public sealed class GcsObjectResourceOptions
{
    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected buckets and objects share one physical coordination domain; an
    /// incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>Resolves the bucket for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? BucketNameSelector { get; init; }

    /// <summary>Resolves the object name for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? ObjectNameSelector { get; init; }

    /// <summary>Resolves an externally owned GCS client for each subject-aware operation.</summary>
    /// <remarks>Use a distinct route when the selected client points to a different project or endpoint.</remarks>
    public Func<ConfiglueResourceContext, StorageClient>? ClientSelector { get; init; }

    /// <summary>The content type applied to uploads. When null, the existing object content type is preserved on overwrite.</summary>
    public string? ContentType { get; init; }

    /// <summary>Resolves the upload content type for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string?>? ContentTypeSelector { get; init; }

    /// <summary>Whether overwrites keep the current object content type when no content type is configured.</summary>
    public bool PreserveContentTypeOnOverwrite { get; init; } = true;

    /// <summary>
    /// The minimum interval between metadata polls while waiting for a change.
    /// Polling observes object generations only and never downloads bodies.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        ValidatePollInterval(PollInterval);

        if (ContentType is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ContentType);
        }
    }

    private static void ValidatePollInterval(TimeSpan pollInterval)
    {
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                "The GCS polling interval must be positive."
            );
        }
    }
}
