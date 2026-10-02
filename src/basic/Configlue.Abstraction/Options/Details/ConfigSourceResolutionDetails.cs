using Configlue.Resources;

namespace Configlue;

/// <summary>Describes the resolved logical subject, provider key, route, and physical resource for one details read.</summary>
public sealed class ConfigSourceResolutionDetails
{
    /// <summary>Creates resolution metadata for one source read.</summary>
    public ConfigSourceResolutionDetails(
        SubjectKey logicalSubjectKey,
        ResourceKey resourceKey,
        RouteKey route,
        ResourceId? resourceId,
        string? physicalOrigin
    )
    {
        LogicalSubjectKey = logicalSubjectKey;
        ResourceKey = resourceKey;
        Route = route;
        ResourceId = resourceId;
        PhysicalOrigin = physicalOrigin;
    }

    /// <summary>The logical subject identity resolved for this read.</summary>
    public SubjectKey LogicalSubjectKey { get; }

    /// <summary>The source-specific provider key used to address this operation.</summary>
    public ResourceKey ResourceKey { get; }

    /// <summary>The intermediate placement route selected for this read; it does not identify the physical resource.</summary>
    public RouteKey Route { get; }

    /// <summary>The identity of the physical resource used for this read, when one is known.</summary>
    public ResourceId? ResourceId { get; }

    /// <summary>Human-readable physical location metadata for diagnostics, when known; it is not a resource identity contract.</summary>
    public string? PhysicalOrigin { get; }

    /// <inheritdoc />
    public override string ToString() => PhysicalOrigin ?? ResourceKey.Value;
}
