using System.Linq.Expressions;
using System.Reflection;
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
/// </remarks>
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

        var path = GetPropertyPath(property);
        EnsureModelRegistered();
        if (ConfiglueModelSchemaCatalog.TryGet(typeof(TModel), out var schema))
        {
            _ = ConfiglueMemberPath.FromNames(schema, path);
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

    private static void EnsureModelRegistered() =>
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(TModel).TypeHandle
        );

    private static string GetPropertyPath<TValue>(Expression<Func<TModel, TValue>> selector)
    {
        Expression expression = selector.Body;
        while (
            expression
                is UnaryExpression
                {
                    NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
                } conversion
        )
        {
            expression = conversion.Operand;
        }

        var members = new Stack<string>();
        while (expression is MemberExpression memberExpression)
        {
            if (memberExpression.Member is not PropertyInfo)
            {
                throw new ArgumentException(
                    "A write route must select model properties.",
                    nameof(selector)
                );
            }

            members.Push(memberExpression.Member.Name);
            expression = memberExpression.Expression!;
        }

        if (expression != selector.Parameters[0] || members.Count == 0)
        {
            throw new ArgumentException(
                "A write route must be a direct or nested model property selector.",
                nameof(selector)
            );
        }

        return string.Join(".", members);
    }
}
