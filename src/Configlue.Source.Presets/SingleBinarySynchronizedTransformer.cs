namespace Configlue.Source.Presets;

internal sealed class SingleBinarySynchronizedTransformer(IStateByteTransformer transformer)
    : IStateByteTransformer,
        IStateByteTransformerRecoveryPolicy
{
    private readonly object _gate = new();

    public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source)
    {
        lock (_gate)
        {
            return transformer.TransformRead(source);
        }
    }

    public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source)
    {
        lock (_gate)
        {
            return transformer.TransformWrite(source);
        }
    }

    public bool IsRecoverableReadException(Exception exception)
    {
        lock (_gate)
        {
            return transformer is IStateByteTransformerRecoveryPolicy recoveryPolicy
                && recoveryPolicy.IsRecoverableReadException(exception);
        }
    }
}
