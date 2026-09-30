namespace Configlue.Transformers;

/// <summary>Transforms serialized state bytes between a resource and a state codec.</summary>
/// <remarks>
/// Read transforms run in registration order. Write transforms run in reverse registration order.
/// The caller owns the transformer instances.
/// </remarks>
public interface IStateByteTransformer
{
    /// <summary>Transforms bytes read from the resource into bytes for the state codec.</summary>
    ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source);

    /// <summary>Transforms codec output into bytes to write to the resource.</summary>
    ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source);
}
