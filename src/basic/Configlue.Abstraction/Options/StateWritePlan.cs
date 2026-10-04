using System.Collections.ObjectModel;
using System.Linq.Expressions;
using Configlue.CompilerServices;

namespace Configlue;

/// <summary>Describes deterministic write ownership for model property paths.</summary>
/// <remarks>
/// The plan combines one default write owner with optional property-path routes. The most specific
/// configured path applies; a route for a nested model also applies to its descendants. Writable mounted
/// sources contribute their mounted subtree as an owned route. Paths without a matching route use the
/// default owner. Registration routes can be combined with per-operation routes; operation routes replace
/// registration routes for the same path. Nested changes can be split across owners. Replacing a nested
/// value with null fails if an owner exists below it.
/// </remarks>
public sealed class StateWritePlan
{
    private readonly KeyValuePair<ConfiglueMemberPath, SourceId>[] _routes = [];
    private readonly ConfiglueModelSchema? _schema;

    /// <summary>Creates a write plan from model property paths to logical source IDs.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public StateWritePlan(IReadOnlyDictionary<string, string> propertyRoutes)
        : this(
            null,
            propertyRoutes.ToDictionary(
                static route => route.Key,
                static route => SourceId.From(route.Value)
            )
        ) { }

    /// <summary>Creates a write plan with a default owner and model property path routes.</summary>
    /// <param name="defaultSourceId">The logical source that owns paths without a more specific route.</param>
    /// <param name="propertyRoutes">Routes from model property paths to logical source IDs.</param>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public StateWritePlan(
        SourceId? defaultSourceId,
        IReadOnlyDictionary<string, SourceId> propertyRoutes
    )
    {
        ArgumentNullException.ThrowIfNull(propertyRoutes);
        if (defaultSourceId is { IsDefault: true })
        {
            throw new ArgumentException(
                "The default source identifier is uninitialized.",
                nameof(defaultSourceId)
            );
        }

        var routes = new Dictionary<string, SourceId>(StringComparer.Ordinal);
        foreach (var route in propertyRoutes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(route.Key);
            if (route.Value.IsDefault)
            {
                throw new ArgumentException(
                    "A routed source identifier is uninitialized.",
                    nameof(propertyRoutes)
                );
            }
            if (
                route
                    .Key.Split(new[] { '.' }, StringSplitOptions.None)
                    .Any(string.IsNullOrWhiteSpace)
            )
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

        DefaultSourceId = defaultSourceId;
        PropertyRoutes = new ReadOnlyDictionary<string, SourceId>(routes);
    }

    /// <summary>Creates a plan with no owners; every changed path has no write target.</summary>
    public static StateWritePlan Empty { get; } =
        new(null, new Dictionary<string, SourceId>(StringComparer.Ordinal));

    /// <summary>Starts a strongly typed write-ownership plan for one generated model.</summary>
    public static StateWritePlanBuilder<TModel> For<TModel>()
        where TModel : IConfiglueModel => new();

    /// <summary>Creates a plan whose default owner is the supplied logical source.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public static StateWritePlan DefaultTo(SourceId sourceId)
    {
        if (sourceId.IsDefault)
        {
            throw new ArgumentException(
                "The source identifier is uninitialized.",
                nameof(sourceId)
            );
        }
        return new StateWritePlan(
            sourceId,
            new Dictionary<string, SourceId>(StringComparer.Ordinal)
        );
    }

    /// <summary>The logical source that owns model paths without a more specific route.</summary>
    public SourceId? DefaultSourceId { get; }

    /// <summary>Configured model property paths and their target logical source IDs.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public IReadOnlyDictionary<string, SourceId> PropertyRoutes { get; }

    /// <summary>Combines this registration plan with owners supplied for one operation.</summary>
    /// <remarks>Operation owners replace registration owners with the same path. Longest-prefix matching still applies.</remarks>
    public StateWritePlan OverrideWith(StateWritePlan overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var routes = PropertyRoutes.ToDictionary(
            static route => route.Key,
            static route => route.Value,
            StringComparer.Ordinal
        );
        foreach (var route in overrides.PropertyRoutes)
        {
            routes[route.Key] = route.Value;
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

        var defaultSourceId = overrides.DefaultSourceId ?? DefaultSourceId;
        var merged = new StateWritePlan(defaultSourceId, routes);
        var schema = _schema ?? overrides._schema;
        return schema is not null ? merged.Bind(schema) : merged;
    }

    /// <summary>Resolves a path using the longest configured path prefix, or returns the default owner.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public SourceId ResolveSourceId(string propertyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        return ResolveSourceIdOrNull(propertyPath, DefaultSourceId)
            ?? throw new InvalidOperationException(
                $"No write owner is configured for model path '{propertyPath}'."
            );
    }

    /// <summary>Resolves a path using the longest configured path prefix, or returns the fallback source ID.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public SourceId ResolveSourceId(string propertyPath, SourceId fallbackSourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        if (fallbackSourceId.IsDefault)
        {
            throw new ArgumentException(
                "The fallback source identifier is uninitialized.",
                nameof(fallbackSourceId)
            );
        }
        return ResolveSourceIdOrNull(propertyPath, fallbackSourceId)!.Value;
    }

    /// <summary>Resolves a compiled generated member path to its configured owner.</summary>
    public SourceId ResolveSourceId(ConfiglueMemberPath path) =>
        ResolveSourceIdOrNull(path)
        ?? throw new InvalidOperationException(
            $"No write owner is configured for model path '{path}'."
        );

    /// <summary>Resolves a compiled generated member path to its owner or the supplied fallback.</summary>
    public SourceId ResolveSourceId(ConfiglueMemberPath path, SourceId fallbackSourceId)
    {
        if (fallbackSourceId.IsDefault)
        {
            throw new ArgumentException(
                "The fallback source identifier is uninitialized.",
                nameof(fallbackSourceId)
            );
        }
        return ResolveSourceIdOrNull(path, fallbackSourceId)!.Value;
    }

    /// <summary>Resolves a path to its most specific owner, or returns the fallback when no owner is configured.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public SourceId? ResolveSourceIdOrNull(string propertyPath, SourceId? fallbackSourceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        if (_schema is not null)
        {
            return ResolveSourceIdOrNull(
                ConfiglueMemberPath.FromNames(_schema, propertyPath),
                fallbackSourceId
            );
        }

        SourceId? result = fallbackSourceId ?? DefaultSourceId;
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

    /// <summary>Resolves a compiled generated member path to its most specific owner.</summary>
    public SourceId? ResolveSourceIdOrNull(
        ConfiglueMemberPath path,
        SourceId? fallbackSourceId = null
    )
    {
        EnsureCompiledPath(path);
        // Routes are sorted longest-first at bind time, so the first prefix match
        // is the longest-prefix (most specific) owner.
        for (var index = 0; index < _routes.Length; index++)
        {
            var route = _routes[index];
            if (route.Key.IsPrefixOf(path))
            {
                return route.Value;
            }
        }
        return fallbackSourceId ?? DefaultSourceId;
    }

    /// <summary>Whether a more specific configured path exists beneath the supplied path.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
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

    /// <summary>Whether a configured generated member path exists beneath the supplied path.</summary>
    public bool HasRouteBelow(ConfiglueMemberPath path)
    {
        EnsureCompiledPath(path);
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

    private StateWritePlan(StateWritePlan plan, ConfiglueModelSchema schema)
    {
        DefaultSourceId = plan.DefaultSourceId;
        PropertyRoutes = plan.PropertyRoutes;
        _schema = schema;
        _routes = new KeyValuePair<ConfiglueMemberPath, SourceId>[PropertyRoutes.Count];
        var index = 0;
        foreach (var route in PropertyRoutes)
        {
            _routes[index++] = new(ConfiglueMemberPath.FromNames(schema, route.Key), route.Value);
        }

        // Longest-prefix ownership resolves by scanning for the longest matching prefix.
        // Sorting longest-first at bind time (once per plan) keeps the per-member hot
        // path a cheap ID-prefix scan with an early exit instead of repeated traversal.
        Array.Sort(
            _routes,
            static (first, second) => second.Key.Length.CompareTo(first.Key.Length)
        );
    }

    internal StateWritePlan WithDefaultSourceId(SourceId? defaultSourceId)
    {
        if (DefaultSourceId == defaultSourceId)
        {
            return this;
        }

        var plan = new StateWritePlan(defaultSourceId, PropertyRoutes);
        return _schema is not null ? plan.Bind(_schema) : plan;
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

    private void EnsureCompiledPath(ConfiglueMemberPath path)
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
