namespace Configlue.Transformers;

/// <summary>Common marker for a state byte transformer.</summary>
/// <remarks>Implement <see cref="ISynchronousStateByteTransformer"/> or <see cref="IAsyncStateByteTransformer"/>.</remarks>
public interface IStateByteTransformer { }

/// <summary>Transforms serialized state bytes synchronously between a resource and a state codec.</summary>
/// <remarks>Read transforms run in registration order; write transforms run in reverse order.</remarks>
public interface ISynchronousStateByteTransformer : IStateByteTransformer
{
    /// <summary>Transforms bytes read from the resource into bytes for the state codec.</summary>
    ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source);

    /// <summary>Transforms codec output into bytes to write to the resource.</summary>
    ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source);
}
