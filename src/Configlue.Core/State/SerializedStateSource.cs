namespace Configlue;

/// <summary>Creates typed state sources by composing a resource with a codec.</summary>
public static class SerializedStateSource
{
    /// <summary>
    /// Creates a source whose state is serialized by <paramref name="codec"/>.
    /// Resource writer and watcher capabilities are connected automatically when available.
    /// </summary>
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
        ResourceId? resourceId = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(codec);

        var resourceWriter = writer ?? resource as IResourceWriter;
        var resourceWatcher = watcher ?? resource as IStateWatcher;
        return new StateSource<T>(
            id,
            new SerializedStateReader<T>(resource, codec, context),
            priority,
            fallbackCondition,
            resourceWriter is null ? null : new SerializedStateWriter<T>(resourceWriter, codec, context),
            resourceWatcher,
            physicalOrigin,
            resourceId ?? (resourceWriter as IResourceIdentity ?? resource as IResourceIdentity)?.ResourceId);
    }
}
