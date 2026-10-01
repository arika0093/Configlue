namespace Configlue.State;

/// <summary>Describes the outcome of reading a state source.</summary>
/// <remarks>
/// <see cref="InvalidPayload"/> is a source-local outcome: the source could be reached but returned a malformed
/// or undecodable payload. It is unrelated to effective-model validation, which is governed solely by
/// <see cref="Configlue.ReadValidationMode"/> and never produces a read status.
/// </remarks>
public enum StateReadStatus
{
    /// <summary>The source returned a value.</summary>
    Success,

    /// <summary>The source does not currently contain a value.</summary>
    NotFound,

    /// <summary>The source could not be reached temporarily.</summary>
    Unavailable,

    /// <summary>The source returned a malformed or undecodable payload.</summary>
    InvalidPayload,
}
