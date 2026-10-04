namespace Configlue.Resource.AwsAppConfig;

/// <summary>A diagnostic snapshot of the AppConfig session. Reading it never affects loaded state.</summary>
/// <param name="LastAttemptUtc">When the session was last polled, if ever.</param>
/// <param name="LastSuccessUtc">When a poll last succeeded, if ever.</param>
/// <param name="LastRevision">The revision of the last successfully loaded payload, if any.</param>
/// <param name="LastPollInterval">The interval the session currently waits between polls.</param>
/// <param name="LastError">The most recent failure message, if any. Null after a success.</param>
/// <param name="SessionRestarts">How many times the session was restarted after an expired token.</param>
/// <param name="PollCount">How many polls were attempted.</param>
/// <param name="ChangeCount">How many polls returned a new payload.</param>
/// <param name="HasValue">Whether a payload was successfully loaded at least once.</param>
public sealed record AwsAppConfigHealthSnapshot(
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    string? LastRevision,
    TimeSpan LastPollInterval,
    string? LastError,
    int SessionRestarts,
    long PollCount,
    long ChangeCount,
    bool HasValue
);
