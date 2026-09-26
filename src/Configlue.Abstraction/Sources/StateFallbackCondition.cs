namespace Configlue;

/// <summary>Read outcomes that allow resolution to continue to a lower-priority source.</summary>
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
