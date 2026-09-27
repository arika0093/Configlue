namespace Configlue;

/// <summary>Describes the outcome of reading a state source.</summary>
public enum StateReadStatus
{
    /// <summary>The source returned a value.</summary>
    Success,

    /// <summary>The source does not currently contain a value.</summary>
    NotFound,

    /// <summary>The source could not be reached temporarily.</summary>
    Unavailable,

    /// <summary>The source returned a value that failed validation.</summary>
    Invalid,
}
