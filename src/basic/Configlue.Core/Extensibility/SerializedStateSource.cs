using Configlue.Codecs;
using Configlue.Sources;

namespace Configlue.Extensibility;

/// <summary>Creates typed state sources by composing a resource, byte transformers, a codec, and middleware.</summary>
public static class SerializedStateSource
{
    /// <summary>
    /// Creates a source whose state is serialized by the typed <paramref name="codec"/>.
    /// Resource writer and watcher capabilities are connected automatically when available.
    /// </summary>
    public static StateSource<T> FromResource<T>(
        string id,
        IResourceReader resource,
        IStateCodec<T> codec,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        IResourceWriter? writer = null,
        ISourceWatcher? watcher = null,
        string? physicalOrigin = null,
        StateCodecContext context = default,
        ResourceId? fixedResourceId = null,
        StateSchemaDispatcher<T>? schemaDispatcher = null,
        IEnumerable<IStateByteTransformer>? transformers = null,
        IEnumerable<IStateMiddleware<T>>? middlewares = null
    ) =>
        FromResource(
            id,
            resource,
            StateCodecBinding.Typed(codec),
            priority,
            fallbackCondition,
            writer,
            watcher,
            physicalOrigin,
            context,
            fixedResourceId,
            schemaDispatcher,
            transformers,
            middlewares
        );

    /// <summary>
    /// Creates a source whose state is serialized by the explicit typed or dynamic
    /// <paramref name="codec"/> binding. Resource writer and watcher capabilities are connected
    /// automatically when available.
    /// </summary>
    /// <param name="id">The stable logical source ID.</param>
    /// <param name="resource">The source of persisted bytes.</param>
    /// <param name="codec">The codec binding that converts between bytes and state values.</param>
    /// <param name="priority">The source read priority.</param>
    /// <param name="fallbackCondition">The read statuses that allow fallback to another source.</param>
    /// <param name="writer">An optional resource writer; detected from <paramref name="resource"/> when omitted.</param>
    /// <param name="watcher">An optional change watcher; detected from <paramref name="resource"/> when omitted.</param>
    /// <param name="physicalOrigin">The physical endpoint backing the source.</param>
    /// <param name="context">Additional codec context.</param>
    /// <param name="fixedResourceId">An optional physical identity override used for every operation context.</param>
    /// <param name="schemaDispatcher">An optional schema migration dispatcher.</param>
    /// <param name="transformers">Byte transformations in resource-to-codec read order.</param>
    /// <param name="middlewares">Typed state decorators; the first middleware is outermost.</param>
    public static StateSource<T> FromResource<T>(
        string id,
        IResourceReader resource,
        StateCodecBinding codec,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        IResourceWriter? writer = null,
        ISourceWatcher? watcher = null,
        string? physicalOrigin = null,
        StateCodecContext context = default,
        ResourceId? fixedResourceId = null,
        StateSchemaDispatcher<T>? schemaDispatcher = null,
        IEnumerable<IStateByteTransformer>? transformers = null,
        IEnumerable<IStateMiddleware<T>>? middlewares = null
    )
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(codec);

        var serialized = new SerializedSource<T>(
            resource,
            codec,
            context,
            schemaDispatcher,
            transformers,
            writer ?? resource as IResourceWriter,
            watcher ?? resource as ISourceWatcher,
            middlewares
        );
        return new StateSource<T>(
            SourceId.From(id),
            serialized,
            priority,
            fallbackCondition,
            serialized.Writer,
            serialized.Watcher,
            physicalOrigin,
            fixedResourceId
        );
    }
}
