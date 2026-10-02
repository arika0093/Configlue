using Configlue.Sources;

namespace Configlue.Extensibility;

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
        ISourceWatcher? watcher = null,
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
            id,
            serialized,
            priority,
            fallbackCondition,
            serialized.Writer,
            serialized.Watcher,
            physicalOrigin,
            resourceId
        );
    }
}
