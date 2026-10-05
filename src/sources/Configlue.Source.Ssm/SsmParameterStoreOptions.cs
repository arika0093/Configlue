using Amazon.SimpleSystemsManagement;

namespace Configlue.Source.Ssm;

/// <summary>Behavior options for a Parameter Store hierarchy source.</summary>
/// <remarks>
/// Credentials and region are resolved by the injected
/// <see cref="IAmazonSimpleSystemsManagement"/> client through the normal AWS
/// credential and region chain. Configlue stores no credentials, no decrypted
/// payloads, and no KMS material.
/// <para>Request retry/resilience is owned by the caller-supplied AWS SDK client.
/// Configlue performs a single SDK call per page or write and maps a throttling
/// or transient outcome reported by the client into an unavailable/error result.
/// Configure retries on the SDK client itself.</para>
/// </remarks>
public sealed class SsmParameterStoreOptions
{
    /// <summary>
    /// Requests <c>SecureString</c> decryption on reads. Defaults to <c>false</c> so callers
    /// can apply least-privilege IAM/KMS policies and only opt in when needed.
    /// </summary>
    public bool WithDecryption { get; init; }

    /// <summary>Loads parameters recursively below the root path. Defaults to <c>true</c>.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>The page size for <c>GetParametersByPath</c> pagination (1-100). Defaults to 10.</summary>
    public int PageSize { get; init; } = 10;

    /// <summary>Whether the source exposes a change watcher. Defaults to <c>false</c> (opt-in polling).</summary>
    public bool WatchChanges { get; init; }

    /// <summary>The polling interval used by the watcher. Defaults to one minute.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The minimum polling interval. Values below this are rejected. Defaults to five seconds.</summary>
    public TimeSpan MinPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The default parameter type for writes. Defaults to <see cref="SsmParameterType.String"/>.</summary>
    public SsmParameterType WriteParameterType { get; init; } = SsmParameterType.String;

    /// <summary>Selects the parameter type for one member path (dotted, for example <c>Database.Host</c>).</summary>
    public Func<string, SsmParameterType>? WriteTypeSelector { get; init; }

    /// <summary>The KMS key ID or ARN used when writing <c>SecureString</c> parameters.</summary>
    /// <remarks>This value is never included in diagnostics, exceptions, or provenance.</remarks>
    public string? WriteKeyId { get; init; }

    /// <summary>Selects the KMS key for one member path. Never included in diagnostics.</summary>
    public Func<string, string?>? WriteKeyIdSelector { get; init; }

    /// <summary>The tier used for writes. Null leaves the AWS default in place.</summary>
    public SsmParameterTier? WriteTier { get; init; }

    /// <summary>The data type used for writes (for example <c>text</c>). Null leaves the AWS default in place.</summary>
    public string? WriteDataType { get; init; }

    /// <summary>
    /// Whether unchecked writes may overwrite existing parameters. Defaults to <c>true</c>.
    /// <c>MustNotExist</c> conditions always use a non-overwriting create; revision-match
    /// conditions are never honored because Parameter Store versions are not atomic
    /// compare-and-swap preconditions.
    /// </summary>
    public bool AllowOverwrite { get; init; } = true;

    /// <summary>Optional scalar conversion override. Types it declines fall back to JSON.</summary>
    public Func<string, Type, object?>? ValueParser { get; init; }

    /// <summary>JSON options used for members without a scalar conversion, such as collections.</summary>
    public System.Text.Json.JsonSerializerOptions? JsonSerializerOptions { get; init; }

    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected root paths share one physical coordination domain; an
    /// incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>Resolves an externally owned SSM client for each subject-aware operation.</summary>
    /// <remarks>Use a distinct route when the selected client points to a different account or region.</remarks>
    public Func<
        ConfiglueResourceContext,
        IAmazonSimpleSystemsManagement
    >? ClientSelector { get; init; }

    internal void Validate()
    {
        ValidatePageSize(PageSize, nameof(PageSize));
        ValidateInterval(PollInterval, nameof(PollInterval));
        ValidateInterval(MinPollInterval, nameof(MinPollInterval));
        ValidatePollFloor(PollInterval, MinPollInterval, nameof(PollInterval));

        ValidateDataType(WriteDataType, nameof(WriteDataType));
    }

    private static void ValidateDataType(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "WriteDataType must contain a value when configured.",
                parameterName
            );
        }
    }

    private static void ValidatePageSize(int value, string parameterName)
    {
        if (value is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "PageSize must be between 1 and 100."
            );
        }
    }

    private static void ValidateInterval(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The interval must be greater than zero."
            );
        }
    }

    private static void ValidatePollFloor(TimeSpan poll, TimeSpan floor, string parameterName)
    {
        if (poll < floor)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "PollInterval must not be below MinPollInterval."
            );
        }
    }
}
