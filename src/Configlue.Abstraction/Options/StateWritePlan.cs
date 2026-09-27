using System.Collections.ObjectModel;

namespace Configlue;

/// <summary>Routes changed model property paths to source-local write targets.</summary>
/// <remarks>
/// The most specific configured path applies. A route for a nested model also applies to its descendants;
/// paths without a matching route use the options instance's configured write source. Registration routes
/// can be combined with per-operation routes; per-operation routes replace registration routes for the same path.
/// Nested changes can be split across routes. Replacing a nested value with null fails if a route exists below it.
/// </remarks>
public sealed class StateWritePlan
{
    private readonly KeyValuePair<string, string>[] _routes;

    /// <summary>Creates a write plan from model property paths to logical source IDs.</summary>
    public StateWritePlan(IReadOnlyDictionary<string, string> propertyRoutes)
    {
        ArgumentNullException.ThrowIfNull(propertyRoutes);
        var routes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var route in propertyRoutes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(route.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(route.Value);
            if (route.Key.Split('.', StringSplitOptions.None).Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    $"Property path '{route.Key}' contains an empty member name.",
                    nameof(propertyRoutes)
                );
            }

            if (!routes.TryAdd(route.Key, route.Value))
            {
                throw new ArgumentException(
                    $"Property path '{route.Key}' is routed more than once.",
                    nameof(propertyRoutes)
                );
            }
        }

        PropertyRoutes = new ReadOnlyDictionary<string, string>(routes);
        _routes = routes.ToArray();
    }

    /// <summary>Creates a plan with no overrides; all changed paths use the configured write source.</summary>
    public static StateWritePlan Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Configured model property paths and their target logical source IDs.</summary>
    public IReadOnlyDictionary<string, string> PropertyRoutes { get; }

    /// <summary>Combines this registration plan with routes supplied for one operation.</summary>
    /// <remarks>Operation routes replace registration routes with the same path. Longest-prefix matching still applies.</remarks>
    public StateWritePlan OverrideWith(StateWritePlan overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var routes = PropertyRoutes.ToDictionary(
            static route => route.Key,
            static route => route.Value,
            StringComparer.Ordinal
        );
        foreach (var (path, sourceId) in overrides.PropertyRoutes)
        {
            routes[path] = sourceId;
        }

        return routes.Count == 0 ? Empty : new StateWritePlan(routes);
    }

    /// <summary>Resolves a path using the longest configured path prefix, or returns the fallback source ID.</summary>
    public string ResolveSourceId(string propertyPath, string fallbackSourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackSourceId);
        var route = _routes
            .Where(candidate =>
                string.Equals(propertyPath, candidate.Key, StringComparison.Ordinal)
                || propertyPath.StartsWith(candidate.Key + ".", StringComparison.Ordinal)
            )
            .OrderByDescending(static candidate => candidate.Key.Length)
            .FirstOrDefault();
        return route.Key is null ? fallbackSourceId : route.Value;
    }

    /// <summary>Whether a more specific configured path exists beneath the supplied path.</summary>
    public bool HasRouteBelow(string propertyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        var prefix = propertyPath + ".";
        return _routes.Any(route => route.Key.StartsWith(prefix, StringComparison.Ordinal));
    }
}
