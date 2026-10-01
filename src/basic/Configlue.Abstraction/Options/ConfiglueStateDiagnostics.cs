using System.Collections.ObjectModel;

namespace Configlue;

/// <summary>An immutable snapshot of the sources and write routing for one state runtime.</summary>
public sealed class ConfiglueStateDiagnostics
{
    private readonly KeyValuePair<string, string>[] _propertyRoutes;

    /// <summary>Creates an immutable source topology snapshot.</summary>
    public ConfiglueStateDiagnostics(
        string stateName,
        IEnumerable<ConfiglueSourceDiagnostics> sources,
        string? defaultWriteSourceId,
        bool defaultWriteSourceIsInferred,
        IReadOnlyDictionary<string, string> propertyWriteRoutes
    )
    {
        ArgumentNullException.ThrowIfNull(stateName);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(propertyWriteRoutes);
        StateName = stateName;
        Sources = Array.AsReadOnly(sources.ToArray());
        DefaultWriteSourceId = defaultWriteSourceId;
        DefaultWriteSourceIsInferred = defaultWriteSourceIsInferred;
        var routes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var route in propertyWriteRoutes)
        {
            var path = route.Key;
            var sourceId = route.Value;
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
            routes.Add(path, sourceId);
        }

        PropertyWriteRoutes = new ReadOnlyDictionary<string, string>(routes);
        _propertyRoutes = routes.ToArray();
    }

    /// <summary>The named state represented by this snapshot.</summary>
    public string StateName { get; }

    /// <summary>Configured sources in read-priority order, including sources retired from this runtime.</summary>
    public IReadOnlyList<ConfiglueSourceDiagnostics> Sources { get; }

    /// <summary>The default write owner, or null when the runtime has no configured writable source.</summary>
    public string? DefaultWriteSourceId { get; }

    /// <summary>Whether the default write owner was inferred from a single writable root source.</summary>
    public bool DefaultWriteSourceIsInferred { get; }

    /// <summary>Registration-level model paths and their configured write owners.</summary>
    public IReadOnlyDictionary<string, string> PropertyWriteRoutes { get; }

    /// <summary>Resolves the configured write owner for a model property path.</summary>
    /// <remarks>Per-operation write plans are not included in this registration snapshot.</remarks>
    public string? GetWriteSourceId(string? propertyPath = null)
    {
        if (propertyPath is null)
        {
            return DefaultWriteSourceId;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        var route = _propertyRoutes
            .Where(candidate =>
                string.Equals(propertyPath, candidate.Key, StringComparison.Ordinal)
                || propertyPath.StartsWith(candidate.Key + ".", StringComparison.Ordinal)
            )
            .OrderByDescending(static candidate => candidate.Key.Length)
            .FirstOrDefault();
        return route.Key is null ? DefaultWriteSourceId : route.Value;
    }
}

/// <summary>Describes one configured source in a state runtime.</summary>
public sealed class ConfiglueSourceDiagnostics
{
    /// <summary>Creates source diagnostics.</summary>
    public ConfiglueSourceDiagnostics(
        string id,
        int priority,
        StateFallbackCondition fallbackCondition,
        bool canRead,
        bool canWrite,
        bool canWatch,
        bool isActive,
        string? physicalOrigin,
        ResourceId? resourceId
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        Priority = priority;
        FallbackCondition = fallbackCondition;
        CanRead = canRead;
        CanWrite = canWrite;
        CanWatch = canWatch;
        IsActive = isActive;
        PhysicalOrigin = physicalOrigin;
        ResourceId = resourceId;
    }

    /// <summary>The stable logical source identifier.</summary>
    public string Id { get; }

    /// <summary>Read priority, where higher values are tried first.</summary>
    public int Priority { get; }

    /// <summary>Statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; }

    /// <summary>Whether the source can be read.</summary>
    public bool CanRead { get; }

    /// <summary>Whether the source can be written.</summary>
    public bool CanWrite { get; }

    /// <summary>Whether the source can report external changes.</summary>
    public bool CanWatch { get; }

    /// <summary>Whether this source is currently active in the runtime.</summary>
    public bool IsActive { get; }

    /// <summary>The physical endpoint or path, when the source provides one.</summary>
    public string? PhysicalOrigin { get; }

    /// <summary>The physical resource identity, when the source provides one.</summary>
    public ResourceId? ResourceId { get; }
}
