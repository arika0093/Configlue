namespace Configlue;

/// <summary>Creates typed state sources by composing a resource, byte transformers, a codec, and middleware.</summary>
public static class SerializedStateSource
{
    /// <summary>
    /// Creates a source whose state is serialized by <paramref name="codec"/>.
    /// Resource writer and watcher capabilities are connected automatically when available.
    /// </summary>
    /// <param name="id">The stable logical source ID.</param>
    /// <param name="resource">The source of persisted bytes.</param>
    /// <param name="codec">The codec that converts between bytes and state values.</param>
    /// <param name="priority">The source read priority.</param>
    /// <param name="fallbackCondition">The read statuses that allow fallback to another source.</param>
    /// <param name="writer">An optional resource writer; detected from <paramref name="resource"/> when omitted.</param>
    /// <param name="watcher">An optional change watcher; detected from <paramref name="resource"/> when omitted.</param>
    /// <param name="physicalOrigin">The physical endpoint backing the source.</param>
    /// <param name="context">Additional codec context.</param>
    /// <param name="resourceId">An optional stable physical resource identity.</param>
    /// <param name="schemaDispatcher">An optional schema migration dispatcher.</param>
    /// <param name="transformers">Byte transformations in resource-to-codec read order.</param>
    /// <param name="middlewares">Typed state decorators; the first middleware is outermost.</param>
    public static StateSource<T> FromResource<T>(
        string id,
        IResourceReader resource,
        object codec,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        IResourceWriter? writer = null,
        IStateWatcher? watcher = null,
        string? physicalOrigin = null,
        StateCodecContext context = default,
        ResourceId? resourceId = null,
        StateSchemaDispatcher<T>? schemaDispatcher = null,
        IEnumerable<IStateByteTransformer>? transformers = null,
        IEnumerable<IStateMiddleware<T>>? middlewares = null
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(codec);

        var resourceWriter = writer ?? resource as IResourceWriter;
        var resourceWatcher = watcher ?? resource as IStateWatcher;
        var transformerPipeline = StateByteTransformerPipeline.Create(transformers);
        var middlewarePipeline = middlewares?.ToArray() ?? [];
        if (middlewarePipeline.Any(static middleware => middleware is null))
        {
            throw new ArgumentException("A middleware collection cannot contain null values.");
        }

        IStateReader<T> reader = new SerializedStateReader<T>(
            resource,
            codec,
            context,
            schemaDispatcher,
            transformerPipeline
        );
        IStateWriter<T>? stateWriter = resourceWriter is null
            ? null
            : new SerializedStateWriter<T>(resourceWriter, codec, context, transformerPipeline);
        for (var index = middlewarePipeline.Length - 1; index >= 0; index--)
        {
            reader =
                middlewarePipeline[index].WrapReader(reader)
                ?? throw new InvalidOperationException(
                    "A middleware returned a null state reader."
                );
            if (stateWriter is not null)
            {
                stateWriter =
                    middlewarePipeline[index].WrapWriter(stateWriter)
                    ?? throw new InvalidOperationException(
                        "A middleware returned a null state writer."
                    );
            }
        }

        return new StateSource<T>(
            id,
            reader,
            priority,
            fallbackCondition,
            stateWriter,
            resourceWatcher,
            physicalOrigin,
            resourceId
                ?? (
                    resourceWriter as IResourceIdentity ?? resource as IResourceIdentity
                )?.ResourceId
        );
    }
}
