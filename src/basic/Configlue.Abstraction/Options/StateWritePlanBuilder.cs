using System.Linq.Expressions;
using System.Reflection;
using Configlue.CompilerServices;

namespace Configlue;

/// <summary>Builds typed source routes for model properties.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
public sealed class StateWritePlanBuilder<TModel>
    where TModel : IConfiglueModel
{
    private readonly Dictionary<string, string> _routes = new(StringComparer.Ordinal);

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

        var path = GetPropertyPath(property);
        _ = ConfiglueMemberPath.FromNames(ConfiglueModelSchemaRegistry<TModel>.Schema, path);
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
    public StateWritePlan Build() =>
        new StateWritePlan(_routes).Bind(ConfiglueModelSchemaRegistry<TModel>.Schema);

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
