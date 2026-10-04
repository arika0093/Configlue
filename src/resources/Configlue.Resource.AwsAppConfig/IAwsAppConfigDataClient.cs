namespace Configlue.Resource.AwsAppConfig;

/// <summary>The result of one AppConfig Data API poll.</summary>
/// <param name="Configuration">The new payload. Empty when the service reports no change.</param>
/// <param name="NextToken">The token to use for the next poll.</param>
/// <param name="NextInterval">How long the client must wait before the next poll.</param>
/// <param name="ContentType">The service-reported content type, when provided.</param>
/// <param name="VersionLabel">The service-reported version label, when provided.</param>
internal sealed record AppConfigPollResult(
    ReadOnlyMemory<byte> Configuration,
    string NextToken,
    TimeSpan NextInterval,
    string? ContentType,
    string? VersionLabel
);

/// <summary>Abstraction over the AppConfig Data API session calls. Implemented by fakes in tests.</summary>
internal interface IAwsAppConfigDataClient
{
    /// <summary>Starts a configuration session and returns the initial token.</summary>
    Task<string> StartSessionAsync(
        string applicationId,
        string environmentId,
        string configurationProfileId,
        int? requiredMinimumPollIntervalInSeconds,
        CancellationToken cancellationToken
    );

    /// <summary>Polls once with the current token.</summary>
    Task<AppConfigPollResult> GetLatestAsync(
        string configurationToken,
        CancellationToken cancellationToken
    );
}
