namespace Configlue.Resource.AwsAppConfig;

/// <summary>Options for an AWS AppConfig configuration-profile resource.</summary>
/// <remarks>
/// <para>
/// The resource is read-only in v1: it consumes deployed configuration through the AppConfig
/// Data API session model (<c>StartConfigurationSession</c> / <c>GetLatestConfiguration</c>)
/// or through the local AppConfig Agent endpoint. It never mutates configuration profiles.
/// </para>
/// <para>
/// <see cref="ClientId"/> is a logical client identity. It participates in resource identity
/// and is sent as the agent <c>Entity-Id</c> header in agent mode. The AppConfig Data API
/// session request has no client-identity field, so the value is not sent to the service in
/// direct mode.
/// </para>
/// </remarks>
public sealed class AwsAppConfigResourceOptions
{
    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected identifiers share one physical coordination domain; an
    /// incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>
    /// A logical client identity for this session (for example a service or replica name).
    /// Part of the resource identity and, in agent mode, the agent <c>Entity-Id</c> header.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// The minimum poll interval requested when the Data API session starts, in seconds.
    /// The service requires a value between 15 and 86400. Null leaves the service default.
    /// Direct mode only.
    /// </summary>
    public int? RequiredMinimumPollIntervalInSeconds { get; init; }

    /// <summary>
    /// The base address of the local AppConfig Agent endpoint
    /// (for example <c>http://localhost:2772</c>). Agent mode only.
    /// </summary>
    public Uri? AgentEndpoint { get; init; }

    /// <summary>
    /// How often the local agent endpoint is polled. The agent manages the service-side
    /// session and token internally, so this local interval replaces the server-directed
    /// next-poll interval used in direct mode. Defaults to 60 seconds. Agent mode only.
    /// </summary>
    public TimeSpan? AgentPollInterval { get; init; }

    internal void Validate(bool agentMode)
    {
        if (ClientId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ClientId);
        }

        ValidateRequiredMinimumPollIntervalInSeconds(
            RequiredMinimumPollIntervalInSeconds,
            agentMode
        );
        ValidateAgentEndpoint(AgentEndpoint, agentMode);
        ValidateAgentPollInterval(AgentPollInterval, agentMode);
    }

    private static void ValidateRequiredMinimumPollIntervalInSeconds(
        int? requiredMinimumPollIntervalInSeconds,
        bool agentMode
    )
    {
        if (requiredMinimumPollIntervalInSeconds is { } minimum)
        {
            if (minimum is < 15 or > 86400)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requiredMinimumPollIntervalInSeconds),
                    "The required minimum poll interval must be between 15 and 86400 seconds."
                );
            }

            if (agentMode)
            {
                throw new ArgumentException(
                    "RequiredMinimumPollIntervalInSeconds only applies to direct Data API mode.",
                    nameof(requiredMinimumPollIntervalInSeconds)
                );
            }
        }
    }

    private static void ValidateAgentEndpoint(Uri? agentEndpoint, bool agentMode)
    {
        if (!agentMode && agentEndpoint is not null)
        {
            throw new ArgumentException(
                "AgentEndpoint only applies to agent mode.",
                nameof(agentEndpoint)
            );
        }
    }

    private static void ValidateAgentPollInterval(TimeSpan? agentPollInterval, bool agentMode)
    {
        if (!agentMode && agentPollInterval is not null)
        {
            throw new ArgumentException(
                "AgentPollInterval only applies to agent mode.",
                nameof(agentPollInterval)
            );
        }

        if (agentPollInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(agentPollInterval),
                "AgentPollInterval must be greater than zero."
            );
        }
    }
}
