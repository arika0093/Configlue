namespace Configlue;

/// <summary>Describes the outcome of a state check or of checking one state source.</summary>
public enum ConfiglueCheckStatus
{
    /// <summary>The value was resolved, or the source contributed to the resolution.</summary>
    Success,

    /// <summary>No value was available from this source or for the resolved state.</summary>
    NotFound,

    /// <summary>The source or resolved state could not be reached temporarily.</summary>
    Unavailable,

    /// <summary>A value was read but failed effective validation.</summary>
    Invalid,

    /// <summary>Reading the source or resolving the state failed with an exception.</summary>
    Faulted,
}
