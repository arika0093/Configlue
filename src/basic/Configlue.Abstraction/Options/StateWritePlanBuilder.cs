using System.Linq.Expressions;
using System.Reflection;
using Configlue.CompilerServices;

namespace Configlue;

/// <summary>Builds deterministic write ownership for one generated model.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
public sealed class StateWritePlanBuilder<TModel>
{
    private readonly Dictionary<string, string> _routes = new(StringComparer.Ordinal);
    private string? _defaultSourceId;

    /// <summary>Sets the default write owner for model paths without a more specific route.</summary>
    public StateWritePlanBuilder<TModel> DefaultTo(SourceKey<TModel> source)
    {
        if (string.IsNullOrWhiteSpace(source.Id))
        {
            throw new ArgumentException("The source key is uninitialized.", nameof(source));
        }

        _defaultSourceId = source.Id;
        return this;
    }

    /// <summary>Sets the default write owner by logical source identifier.</summary>
    /// <remarks>Prefer the typed <see cref="DefaultTo(SourceKey{TModel})"/> overload when a source key is available.</remarks>
    public StateWritePlanBuilder<TModel> DefaultTo(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        _defaultSourceId = sourceId;
        return this;
    }

    /// <summary>Routes a model property and its descendants to a source.</summary>
    public StateWritePlanBuilder<TModel> Route<TValue>(
        Expression<Func<TModel, TValue>> property,
        SourceKey<TModel> source
    )
    {
        ArgumentNullException.ThrowIfNull(property);
        if (string.IsNullOrWhiteSpace(source.Id))
        {
            throw new ArgumentException("The source key is uninitialized.", nameof(source));
        }

        return Route(property, source.Id);
    }

    /// <summary>Routes a model property and its descendants to a logical source identifier.</summary>
    public StateWritePlanBuilder<TModel> Route<TValue>(
        Expression<Func<TModel, TValue>> property,
        string sourceId
    )
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var path = GetPropertyPath(property);
        if (ConfiglueModelSchemaCatalog.TryGet(typeof(TModel), out var schema))
        {
            _ = ConfiglueMemberPath.FromNames(schema, path);
        }

        if (!_routes.TryAdd(path, sourceId))
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
        var plan = new StateWritePlan(_defaultSourceId, _routes);
        return ConfiglueModelSchemaCatalog.TryGet(typeof(TModel), out var schema)
            ? plan.Bind(schema)
            : plan;
    }

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
