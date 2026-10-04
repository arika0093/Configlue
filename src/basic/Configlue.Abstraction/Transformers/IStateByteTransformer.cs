using System.Buffers;

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

/// <summary>A synchronous transformer that can write directly into caller-provided output storage.</summary>
/// <remarks>
/// This is an advanced performance contract, not part of the canonical provider composition path
/// (see <see cref="Codecs.StateCodecBinding"/> composition via the canonical serialized source).
/// A transformer works fully without it via <see cref="ISynchronousStateByteTransformer"/>. Implement
/// it only when writing into caller-provided pooled storage avoids an extra allocation for large
/// payloads.
/// <para>
/// Lifetime and buffering: the transformer must retain neither the source span nor the destination.
/// The destination owns the produced bytes; callers control its lifetime and may provide pooled
/// storage. Implementations must append exactly the transformed output and must not complete or
/// dispose the destination.
/// </para>
/// </remarks>
public interface IDestinationStateByteTransformer : ISynchronousStateByteTransformer
{
    /// <summary>Transforms resource bytes into caller-provided output storage.</summary>
    void TransformRead(ReadOnlySpan<byte> source, IBufferWriter<byte> destination);

    /// <summary>Transforms serialized bytes into caller-provided output storage.</summary>
    void TransformWrite(ReadOnlySpan<byte> source, IBufferWriter<byte> destination);
}
