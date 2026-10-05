using Amazon.SecretsManager;

namespace Configlue.Resource.SecretsManager;

/// <summary>Options for a resource backed by one AWS Secrets Manager secret.</summary>
/// <remarks>
/// Reads default to the <c>AWSCURRENT</c> staging label. Configuring <see cref="VersionId"/>
/// pins the resource to one immutable secret version.
/// <para>Request retry/resilience is owned by the caller-supplied AWS SDK client.
/// Configlue performs a single SDK call per read, write, or metadata poll and maps
/// a throttling or transient outcome reported by the client into an unavailable
/// result. Configure retries on the SDK client itself.</para>
/// <para>
/// Least-privilege IAM for read-only mode:
/// <c>secretsmanager:GetSecretValue</c> and <c>secretsmanager:DescribeSecret</c> on the secret,
/// plus <c>kms:Decrypt</c> on its KMS key when the secret uses a customer managed key.
/// Writable mode additionally requires <c>secretsmanager:PutSecretValue</c> on the secret and,
/// when staging-label promotion is used, <c>secretsmanager:UpdateSecretVersionStage</c>.
/// </para>
/// </remarks>
public sealed class SecretsManagerResourceOptions
{
    /// <summary>The staging label used for moving reads and rotation polling.</summary>
    public const string CurrentVersionStage = "AWSCURRENT";

    /// <summary>
    /// A fixed secret version ID. Fixed versions are immutable and never create a watcher.
    /// When set, <see cref="VersionStage"/> and <see cref="VersionStageSelector"/> are ignored.
    /// </summary>
    public string? VersionId { get; init; }

    /// <summary>
    /// The staging label resolved for each moving read. Defaults to <c>AWSCURRENT</c>.
    /// </summary>
    public string VersionStage { get; init; } = CurrentVersionStage;

    /// <summary>Resolves the secret ARN or name for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? SecretIdSelector { get; init; }

    /// <summary>Resolves a fixed secret version ID for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string?>? VersionIdSelector { get; init; }

    /// <summary>Resolves the staging label for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string>? VersionStageSelector { get; init; }

    /// <summary>Resolves an externally owned Secrets Manager client for each subject-aware operation.</summary>
    /// <remarks>Use a distinct route when the selected client points to a different AWS account or region.</remarks>
    public Func<ConfiglueResourceContext, IAmazonSecretsManager>? ClientSelector { get; init; }

    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected secrets and versions share one physical coordination domain; an
    /// incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>
    /// Whether moving staging labels expose a change watcher. Fixed versions never watch,
    /// regardless of this setting.
    /// </summary>
    public bool WatchEnabled { get; init; } = true;

    /// <summary>
    /// The minimum interval between version-metadata checks while watching a moving staging
    /// label. Higher values reduce Secrets Manager cost during rotation polling.
    /// </summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The staging labels attached to versions created by writes. Defaults to
    /// <c>AWSCURRENT</c>, which moves the label to the new version using normal
    /// Secrets Manager semantics.
    /// </summary>
    public IReadOnlyList<string> WriteVersionStages { get; init; } = [CurrentVersionStage];

    /// <summary>
    /// Whether writes persist content as <c>SecretBinary</c>. Otherwise content is
    /// persisted as <c>SecretString</c> using UTF-8. Reads accept both forms.
    /// </summary>
    public bool UseSecretBinary { get; init; }

    internal void Validate()
    {
        if (VersionId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(VersionId);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(VersionStage);
        ArgumentNullException.ThrowIfNull(WriteVersionStages);
        foreach (var stage in WriteVersionStages)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PollingInterval, TimeSpan.Zero);
    }
}
