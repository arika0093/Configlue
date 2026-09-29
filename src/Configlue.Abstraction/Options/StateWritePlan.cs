using System.Collections.ObjectModel;
using System.Linq.Expressions;

namespace Configlue;

/// <summary>Routes changed model property paths to source-local write targets.</summary>
/// <remarks>
/// The most specific configured path applies. A route for a nested model also applies to its descendants;
/// paths without a matching route use the state instance's configured write source. Registration routes
/// can be combined with per-operation routes; per-operation routes replace registration routes for the same path.
/// Nested changes can be split across routes. Replacing a nested value with null fails if a route exists below it.
/// </remarks>
public sealed class StateWritePlan
{
    private readonly KeyValuePair<ConfiglueMemberPath, string>[] _routes = [];
    private readonly ConfiglueModelSchema? _schema;

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
    }

    /// <summary>Creates a plan with no overrides; all changed paths use the configured write source.</summary>
    public static StateWritePlan Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Starts a strongly typed write-routing plan for one generated model.</summary>
    public static StateWritePlanBuilder<TModel> For<TModel>()
        where TModel : IConfiglueModel => new();

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

        if (
            _schema is not null
            && overrides._schema is not null
            && !ConfiglueMemberPath
                .Root(_schema)
                .SameRoot(ConfiglueMemberPath.Root(overrides._schema))
        )
        {
            throw new ArgumentException(
                "Write plans belong to different generated root models.",
                nameof(overrides)
            );
        }
        var merged = routes.Count == 0 ? Empty : new StateWritePlan(routes);
        var schema = _schema ?? overrides._schema;
        return schema is not null ? merged.Bind(schema) : merged;
    }

    /// <summary>Resolves a path using the longest configured path prefix, or returns the fallback source ID.</summary>
    public string ResolveSourceId(string propertyPath, string fallbackSourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackSourceId);
        return ResolveSourceIdOrNull(propertyPath, fallbackSourceId)!;
    }

    /// <summary>Resolves a path to its most specific source, or returns null when no owner is configured.</summary>
    public string? ResolveSourceIdOrNull(string propertyPath, string? fallbackSourceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        if (_schema is not null)
        {
            return ResolveSourceIdOrNull(
                ConfiglueMemberPath.FromNames(_schema, propertyPath),
                fallbackSourceId
            );
        }
        string? result = fallbackSourceId;
        var length = -1;
        foreach (var route in PropertyRoutes)
        {
            if (route.Key.Length > length && IsPathOrDescendant(propertyPath, route.Key))
            {
                result = route.Value;
                length = route.Key.Length;
            }
        }
        return result;
    }

    /// <summary>Whether a more specific configured path exists beneath the supplied path.</summary>
    public bool HasRouteBelow(string propertyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        foreach (var routeKey in PropertyRoutes.Keys)
        {
            if (
                routeKey.Length > propertyPath.Length
                && routeKey[propertyPath.Length] == '.'
                && routeKey.AsSpan(0, propertyPath.Length).SequenceEqual(propertyPath.AsSpan())
            )
            {
                return true;
            }
        }
        return false;
    }

    private StateWritePlan(StateWritePlan plan, ConfiglueModelSchema schema)
    {
        PropertyRoutes = plan.PropertyRoutes;
        _schema = schema;
        _routes = new KeyValuePair<ConfiglueMemberPath, string>[PropertyRoutes.Count];
        var index = 0;
        foreach (var route in PropertyRoutes)
        {
            _routes[index++] = new(ConfiglueMemberPath.FromNames(schema, route.Key), route.Value);
        }
    }

    internal StateWritePlan Bind(ConfiglueModelSchema schema)
    {
        if (_schema is not null)
        {
            if (!ConfiglueMemberPath.Root(_schema).SameRoot(ConfiglueMemberPath.Root(schema)))
            {
                throw new ArgumentException(
                    "Write plan belongs to a different generated root model.",
                    nameof(schema)
                );
            }
            return this;
        }
        return new StateWritePlan(this, schema);
    }

    internal string? ResolveSourceIdOrNull(
        ConfiglueMemberPath path,
        string? fallbackSourceId = null
    )
    {
        if (_schema is null)
        {
            throw new InvalidOperationException(
                "Compile the write plan before generated path lookup."
            );
        }
        if (!ConfiglueMemberPath.Root(_schema).SameRoot(path))
        {
            throw new ArgumentException(
                "The path belongs to a different root model.",
                nameof(path)
            );
        }
        var bestLength = -1;
        var result = fallbackSourceId;
        for (var index = 0; index < _routes.Length; index++)
        {
            var route = _routes[index];
            if (route.Key.Length > bestLength && route.Key.IsPrefixOf(path))
            {
                bestLength = route.Key.Length;
                result = route.Value;
            }
        }
        return result;
    }

    internal bool HasRouteBelow(ConfiglueMemberPath path)
    {
        for (var index = 0; index < _routes.Length; index++)
        {
            var route = _routes[index].Key;
            if (route.Length > path.Length && path.IsPrefixOf(route))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPathOrDescendant(string propertyPath, string routeKey)
    {
        if (propertyPath.Length < routeKey.Length)
        {
            return false;
        }

        if (!propertyPath.AsSpan(0, routeKey.Length).SequenceEqual(routeKey.AsSpan()))
        {
            return false;
        }

        return propertyPath.Length == routeKey.Length || propertyPath[routeKey.Length] == '.';
    }
}
