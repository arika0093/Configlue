using System.Linq.Expressions;
using Configlue.CompilerServices;

namespace Configlue;

/// <summary>Builds deterministic write ownership for one generated model.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <remarks>
/// Read priority never selects a write destination. Ordinary writes resolve their owner as follows:
/// <list type="number">
/// <item>A writable mounted source owns its mounted subtree.</item>
/// <item>An explicit property route configured here routes that path to the selected source.</item>
/// <item>Remaining root-level changes go to the configured <see cref="DefaultTo(SourceKey{TModel})"/> owner.</item>
/// <item>If exactly one non-explicit writable root source exists, it is inferred automatically.</item>
/// <item>If multiple non-explicit writable root sources exist and no default is configured, context creation fails as ambiguous.</item>
/// <item>Explicit-only sources are excluded from ordinary ownership inference and are writable only through explicit source operations.</item>
/// </list>
/// Once ownership selects a source, a write that cannot realize the requested edit because of
/// higher-priority or read-only contributions fails as a conflict instead of silently choosing
/// another persistence target.
/// Advanced vocabulary for explicit write-ownership configuration.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class StateWritePlanBuilder<TModel>
{
    private readonly Dictionary<string, SourceId> _routes = new(StringComparer.Ordinal);
    private SourceId? _defaultSourceId;

    /// <summary>Sets the default write owner for model paths without a more specific route.</summary>
    public StateWritePlanBuilder<TModel> DefaultTo(SourceKey<TModel> source)
    {
        if (source.IsDefault)
        {
            throw new ArgumentException("The source key is uninitialized.", nameof(source));
        }

        _defaultSourceId = source.Id;
        return this;
    }

    /// <summary>Routes a model property and its descendants to a source.</summary>
    public StateWritePlanBuilder<TModel> Route<TValue>(
        Expression<Func<TModel, TValue>> property,
        SourceKey<TModel> source
    )
    {
        ArgumentNullException.ThrowIfNull(property);
        if (source.IsDefault)
        {
            throw new ArgumentException("The source key is uninitialized.", nameof(source));
        }

        var names = ConfiglueMemberSelector.GetMemberNames(
            property,
            "write route",
            nameof(property)
        );
        var path = string.Join(".", names);
        EnsureModelRegistered();
        if (ConfiglueModelSchemaCatalog.TryGet(typeof(TModel), out var schema))
        {
            _ = ConfiglueMemberPath.FromMemberNames(schema, names, nameof(property));
        }

        if (!_routes.TryAdd(path, source.Id))
        {
            throw new ArgumentException(
                $"Property '{path}' is routed more than once.",
                nameof(property)
            );
        }

        return this;
    }

    /// <summary>Creates the immutable write plan.</summary>
    public StateWritePlan Build()
    {
        EnsureModelRegistered();
        var plan = new StateWritePlan(_defaultSourceId, _routes);
        return ConfiglueModelSchemaCatalog.TryGet(typeof(TModel), out var schema)
            ? plan.Bind(schema)
            : plan;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2059",
        Justification = "The handle comes from typeof(TModel), a generated model already rooted by this closed generic instantiation. Running its static constructor only triggers generated registration."
    )]
    private static void EnsureModelRegistered() =>
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(TModel).TypeHandle
        );
}
