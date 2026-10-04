namespace Configlue.Sources;

/// <summary>
/// The single internal copy/reconfigure primitive for <see cref="StateSource{T}"/> descriptors.
/// </summary>
/// <remarks>
/// All descriptor reconstruction (registration overrides, source-set keying, projection/composition,
/// write-ownership/model-id/key-selector adjustments) flows through <see cref="Reconfigure{TSource, TTarget}"/>.
/// Callers pass a new reader/writer plus only the fields they override; every other
/// metadata/capability field is preserved from the source descriptor. Adding a new
/// <see cref="StateSourceOptions{T}"/> field only requires updating this one method.
/// <para>
/// Dynamic/contextual identity is never frozen: the fixed <c>ResourceId</c> override is copied
/// as-is (null stays contextual) and resource-key/route selectors are carried as delegates
/// (<c>source.GetResourceKey</c> / <c>source.GetRouteKey</c>), so per-subject resolution still
/// delegates to the original mapping. This preserves the behavior fixed by #101.
/// </para>
/// </remarks>
internal static class StateSourceReconfiguration
{
    internal static StateSource<TTarget> Reconfigure<TSource, TTarget>(
        StateSource<TSource> source,
        ISourceReader<TTarget> reader,
        ISourceWriter<TTarget>? writer,
        SourceId? id = null,
        int? priority = null,
        StateFallbackCondition? fallbackCondition = null,
        bool? explicitOnly = null,
        Func<IConfiglueSubject, ResourceKey>? resourceKeySelector = null,
        RuntimeLifetimeRequirement? runtimeLifetime = null,
        string? modelId = null,
        bool replaceModelId = false,
        string[]? ownedPropertyPaths = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reader);
        var target = new StateSource<TTarget>(
            id ?? source.Id,
            reader,
            new StateSourceOptions<TTarget>
            {
                Priority = priority ?? source.Priority,
                FallbackCondition = fallbackCondition ?? source.FallbackCondition,
                Writer = writer,
                DisableWriteCapability = writer is null,
                Watcher = source.Watcher,
                PhysicalOrigin = source.PhysicalOrigin,
                FixedResourceId = source.ConfiguredResourceId,
                ExplicitOnly = explicitOnly ?? source.ExplicitOnly,
                ResourceKeySelector =
                    resourceKeySelector
                    ?? (Func<IConfiglueSubject, ResourceKey>)source.GetResourceKey,
                RuntimeLifetime = runtimeLifetime ?? source.RuntimeLifetime,
                ModelId = replaceModelId ? modelId : (modelId ?? source.ModelId),
                RouteSelector = (Func<IConfiglueSubject, RouteKey>)source.GetRouteKey,
            }
        );
        target.SetOwnedPropertyPaths(ownedPropertyPaths ?? [.. source.OwnedPropertyPaths]);
        return target;
    }
}
