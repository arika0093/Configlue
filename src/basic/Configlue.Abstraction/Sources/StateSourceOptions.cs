using Configlue.Resources;

namespace Configlue.Sources;

/// <summary>Optional routing and capability settings for a logical state source.</summary>
public sealed class StateSourceOptions<T>
{
    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow the next source to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>An optional writer, overriding a writer supplied by the reader object.</summary>
    public ISourceWriter<T>? Writer { get; init; }

    /// <summary>Prevents detection of a writer supplied by the reader object.</summary>
    public bool DisableWriteCapability { get; init; }

    /// <summary>An optional watcher, overriding a watcher supplied by the reader object.</summary>
    public ISourceWatcher? Watcher { get; init; }

    /// <summary>Human-readable physical location metadata for diagnostics.</summary>
    public string? PhysicalOrigin { get; init; }

    /// <summary>An explicit physical identity override shared by all operation contexts.</summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>Descriptor used to distinguish registrations of the same reader instance.</summary>
    public string? LogicalDescriptor { get; init; }

    /// <summary>Whether this source is excluded from ordinary inferred write routing.</summary>
    public bool ExplicitOnly { get; init; }

    /// <summary>Resolves this source's key for an application-defined subject.</summary>
    public Func<IConfiglueSubject, ResourceKey>? ResourceKeySelector { get; init; }

    /// <summary>The dependency-injection lifetime required by this source.</summary>
    public RuntimeLifetimeRequirement RuntimeLifetime { get; init; } =
        RuntimeLifetimeRequirement.Shared;

    /// <summary>The stable Configlue model ID backing this logical source, when known.</summary>
    public string? ModelId { get; init; }

    /// <summary>Resolves this source's physical route for an application-defined subject.</summary>
    public Func<IConfiglueSubject, RouteKey>? RouteSelector { get; init; }
}
