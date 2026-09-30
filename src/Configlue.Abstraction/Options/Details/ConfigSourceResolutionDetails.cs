using Configlue.Resources;

namespace Configlue;

/// <summary>Describes the resolved subject routing and physical placement used by one details read.</summary>
public sealed class ConfigSourceResolutionDetails
{
    /// <summary>Creates resolution metadata for one source read.</summary>
    public ConfigSourceResolutionDetails(
        SubjectKey logicalSubjectKey,
        SubjectKey resourceKey,
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

    /// <summary>The source-specific key used to read this source for the resolved subject.</summary>
    public SubjectKey ResourceKey { get; }

    /// <summary>The physical placement route selected for this read.</summary>
    public RouteKey Route { get; }

    /// <summary>The identity of the physical resource used for this read, when one is known.</summary>
    public ResourceId? ResourceId { get; }

    /// <summary>The physical origin used for this read, when one is known.</summary>
    public string? PhysicalOrigin { get; }

    /// <inheritdoc />
    public override string ToString() => PhysicalOrigin ?? ResourceKey.Value;
}
