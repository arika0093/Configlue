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
            content = transformer.TransformRead(content);
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
            content = transformers[index].TransformWrite(content);
        }

        return content;
    }
}
