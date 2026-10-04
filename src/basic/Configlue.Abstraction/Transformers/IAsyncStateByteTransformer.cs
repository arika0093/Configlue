namespace Configlue.Transformers;

/// <summary>Transforms serialized state bytes using asynchronous dependencies.</summary>
/// <remarks>
/// Transformers run in registration order when reading and reverse registration order when writing.
/// When an instance implements both transformer contracts, the asynchronous methods take precedence.
/// Synchronous-only transformers continue to use <see cref="IStateByteTransformer"/> without an
/// asynchronous state machine. The caller owns transformer instances.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IAsyncStateByteTransformer : IStateByteTransformer
{
    /// <summary>Transforms bytes read from the resource into bytes for the state codec.</summary>
    ValueTask<ReadOnlyMemory<byte>> TransformReadAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default
    );

    /// <summary>Transforms codec output into bytes to write to the resource.</summary>
    ValueTask<ReadOnlyMemory<byte>> TransformWriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default
    );
}
