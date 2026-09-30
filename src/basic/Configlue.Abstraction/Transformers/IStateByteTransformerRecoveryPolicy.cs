namespace Configlue.Transformers;

/// <summary>Classifies byte transformation failures that may be recovered from a validated backup.</summary>
public interface IStateByteTransformerRecoveryPolicy
{
    /// <summary>Whether the exception represents malformed or unauthenticated transformed input.</summary>
    bool IsRecoverableReadException(Exception exception);
}
