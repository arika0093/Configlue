namespace Configlue.Sources;

/// <summary>Source-local read outcomes that allow resolution to continue to a lower-priority source.</summary>
/// <remarks>
/// These conditions describe how a state source itself failed to supply a usable value. They never describe
/// effective-model validation: failures raised by DataAnnotations or <see cref="Configlue.IConfiglueValidator{T}"/>
/// are handled solely by <see cref="Configlue.ReadValidationMode"/> and never cause fallback.
/// </remarks>
[Flags]
public enum StateFallbackCondition
{
    /// <summary>Do not fall back for any result.</summary>
    None = 0,

    /// <summary>Continue after a source reports that it has no state.</summary>
    NotFound = 1,

    /// <summary>Continue after a source reports temporary unavailability.</summary>
    Unavailable = 2,

    /// <summary>Continue after either a missing state or temporary unavailability.</summary>
    NotFoundOrUnavailable = NotFound | Unavailable,

    /// <summary>
    /// Continue after a source reports a malformed or undecodable payload
    /// (<see cref="Configlue.State.StateReadStatus.InvalidPayload"/>). This is a source-local read outcome and
    /// is unrelated to effective-model validation.
    /// </summary>
    InvalidPayload = 4,
}
