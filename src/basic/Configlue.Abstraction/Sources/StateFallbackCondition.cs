namespace Configlue.Sources;

/// <summary>Source-local read outcomes that allow resolution to continue to a lower-priority source.</summary>
/// <remarks>
/// These conditions describe how a state source itself failed to supply a usable value. They never describe
/// effective-model validation: failures raised by DataAnnotations or validators
/// throw a validation exception and never cause fallback.
/// <para>
/// Malformed or undecodable payloads (<see cref="Configlue.State.StateReadStatus.InvalidPayload"/>) never
/// cause fallback. A high-priority source that reports <c>InvalidPayload</c> fails resolution visibly
/// so configuration corruption cannot be silently hidden by a lower-priority source (see issue #312).
/// There is no opt-in fallback for this status through ordinary resolution.
/// </para>
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
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
}
