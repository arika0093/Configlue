namespace Configlue.Codecs;

/// <summary>Classifies serialized input failures that may be recovered from a validated file backup.</summary>
/// <remarks>
/// Implementations should return <see langword="true"/> only for failures caused by invalid serialized
/// input. Schema incompatibility, invalid codec configuration, and application errors must remain failures.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IStateCodecRecoveryPolicy
{
    /// <summary>Whether the exception represents malformed serialized input that can use backup recovery.</summary>
    bool IsRecoverableReadException(Exception exception);
}
