using System.Runtime.CompilerServices;

namespace Configlue.State;

internal static class StateByteTransformerPipeline
{
    private static readonly ConditionalWeakTable<
        IReadOnlyList<IStateByteTransformer>,
        AsyncCapability
    > AsyncCapabilities = new();

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

        _ = AsyncCapabilities.GetValue(
            items,
            static values => new AsyncCapability(HasAsyncSlow(values))
        );
        return items;
    }

    public static bool HasAsync(IReadOnlyList<IStateByteTransformer> transformers) =>
        AsyncCapabilities
            .GetValue(transformers, static values => new AsyncCapability(HasAsyncSlow(values)))
            .ContainsAsync;

    private static bool HasAsyncSlow(IReadOnlyList<IStateByteTransformer> transformers)
    {
        for (var index = 0; index < transformers.Count; index++)
        {
            if (transformers[index] is IAsyncStateByteTransformer)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class AsyncCapability(bool hasAsync)
    {
        public bool ContainsAsync { get; } = hasAsync;
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
        HasAsync(transformers)
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
        HasAsync(transformers)
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
