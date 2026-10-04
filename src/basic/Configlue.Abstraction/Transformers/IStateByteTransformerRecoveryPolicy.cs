namespace Configlue.Transformers;

/// <summary>Classifies byte transformation failures that may be recovered from a validated backup.</summary>
/// <remarks>Advanced provider SPI.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IStateByteTransformerRecoveryPolicy
{
    /// <summary>Whether the exception represents malformed or unauthenticated transformed input.</summary>
    bool IsRecoverableReadException(Exception exception);
}
