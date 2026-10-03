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
        if (items.Any(static transformer => transformer is null))
        {
            throw new ArgumentException("A transformer collection cannot contain null values.");
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
            content = ((ISynchronousStateByteTransformer)transformer).TransformRead(content);
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
            content = ((ISynchronousStateByteTransformer)transformers[index]).TransformWrite(
                content
            );
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
                : ((ISynchronousStateByteTransformer)transformer).TransformRead(content);
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
                : ((ISynchronousStateByteTransformer)transformer).TransformWrite(content);
        }
        return content;
    }
}
