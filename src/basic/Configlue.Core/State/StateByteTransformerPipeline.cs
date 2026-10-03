using System.Buffers;

namespace Configlue.State;

internal static class StateByteTransformerPipeline
{
    public static IStateByteTransformer[] Create(IEnumerable<IStateByteTransformer>? transformers)
    {
        if (transformers is null)
        {
            return [];
        }

        var items = transformers.ToArray();
        if (
            items.Any(static transformer =>
                transformer is null
                || (
                    transformer is not ISynchronousStateByteTransformer
                    && transformer is not IAsyncStateByteTransformer
                )
            )
        )
        {
            throw new ArgumentException(
                "A transformer collection cannot contain null values or values that implement neither synchronous nor asynchronous transformer capabilities."
            );
        }

        return items;
    }

    public static ReadOnlyMemory<byte> TransformRead(
        ReadOnlyMemory<byte> content,
        IReadOnlyList<IStateByteTransformer> transformers
    )
    {
        foreach (var transformer in transformers)
        {
            content = TransformSynchronousRead(transformer, content);
        }

        return content;
    }

    public static ReadOnlyMemory<byte> TransformWrite(
        ReadOnlyMemory<byte> content,
        IReadOnlyList<IStateByteTransformer> transformers
    )
    {
        for (var index = transformers.Count - 1; index >= 0; index--)
        {
            content = TransformSynchronousWrite(transformers[index], content);
        }

        return content;
    }

    public static ValueTask<ReadOnlyMemory<byte>> TransformReadAsync(
        ReadOnlyMemory<byte> content,
        IReadOnlyList<IStateByteTransformer> transformers,
        CancellationToken cancellationToken
    ) =>
        transformers.Any(static transformer => transformer is IAsyncStateByteTransformer)
            ? TransformReadAsyncCore(content, transformers, cancellationToken)
            : new ValueTask<ReadOnlyMemory<byte>>(TransformRead(content, transformers));

    private static async ValueTask<ReadOnlyMemory<byte>> TransformReadAsyncCore(
        ReadOnlyMemory<byte> content,
        IReadOnlyList<IStateByteTransformer> transformers,
        CancellationToken cancellationToken
    )
    {
        foreach (var transformer in transformers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            content = transformer is IAsyncStateByteTransformer asyncTransformer
                ? await asyncTransformer
                    .TransformReadAsync(content, cancellationToken)
                    .ConfigureAwait(false)
                : TransformSynchronousRead(transformer, content);
        }
        return content;
    }

    public static ValueTask<ReadOnlyMemory<byte>> TransformWriteAsync(
        ReadOnlyMemory<byte> content,
        IReadOnlyList<IStateByteTransformer> transformers,
        CancellationToken cancellationToken
    ) =>
        transformers.Any(static transformer => transformer is IAsyncStateByteTransformer)
            ? TransformWriteAsyncCore(content, transformers, cancellationToken)
            : new ValueTask<ReadOnlyMemory<byte>>(TransformWrite(content, transformers));

    private static async ValueTask<ReadOnlyMemory<byte>> TransformWriteAsyncCore(
        ReadOnlyMemory<byte> content,
        IReadOnlyList<IStateByteTransformer> transformers,
        CancellationToken cancellationToken
    )
    {
        for (var index = transformers.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transformer = transformers[index];
            content = transformer is IAsyncStateByteTransformer asyncTransformer
                ? await asyncTransformer
                    .TransformWriteAsync(content, cancellationToken)
                    .ConfigureAwait(false)
                : TransformSynchronousWrite(transformer, content);
        }
        return content;
    }

    private static ReadOnlyMemory<byte> TransformSynchronousRead(
        IStateByteTransformer transformer,
        ReadOnlyMemory<byte> content
    )
    {
        if (transformer is not IDestinationStateByteTransformer destinationTransformer)
        {
            return ((ISynchronousStateByteTransformer)transformer).TransformRead(content);
        }

        var destination = new ArrayBufferWriter<byte>(Math.Max(1, content.Length));
        destinationTransformer.TransformRead(content.Span, destination);
        return destination.WrittenMemory;
    }

    private static ReadOnlyMemory<byte> TransformSynchronousWrite(
        IStateByteTransformer transformer,
        ReadOnlyMemory<byte> content
    )
    {
        if (transformer is not IDestinationStateByteTransformer destinationTransformer)
        {
            return ((ISynchronousStateByteTransformer)transformer).TransformWrite(content);
        }

        var destination = new ArrayBufferWriter<byte>(Math.Max(1, content.Length));
        destinationTransformer.TransformWrite(content.Span, destination);
        return destination.WrittenMemory;
    }
}
