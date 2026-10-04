using System.Linq.Expressions;
using System.Reflection;

namespace Configlue.CompilerServices;

/// <summary>
/// The single parser for typed member selectors (<c>Expression&lt;Func&lt;TModel, ...&gt;&gt;</c>).
/// </summary>
/// <remarks>
/// Every caller that converts a lambda selector into a dotted property path or a bound
/// <see cref="ConfiglueMemberPath"/> goes through this helper so unwrapping, member
/// validation, and diagnostics stay consistent. Strings are produced only for callers
/// whose API or storage genuinely requires them; schema binding consumes the parsed
/// member names directly without a <c>string.Join</c>/<c>Split</c> round-trip.
/// No runtime reflection-based member traversal is performed: member identity comes
/// from the compiled expression nodes and generated schema metadata.
/// </remarks>
internal static class ConfiglueMemberSelector
{
    internal static string[] GetMemberNames<TModel, TValue>(
        Expression<Func<TModel, TValue>> selector,
        string selectorDescription,
        string paramName
    )
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectorDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(paramName);

        Expression expression = selector.Body;
        while (
            expression
                is UnaryExpression
                {
                    NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                } conversion
        )
        {
            expression = conversion.Operand;
        }

        var segments = new Stack<string>();
        while (expression is MemberExpression memberExpression)
        {
            if (memberExpression.Member is not PropertyInfo)
            {
                throw new ArgumentException(
                    $"A {selectorDescription} must use generated model properties.",
                    paramName
                );
            }

            segments.Push(memberExpression.Member.Name);
            expression = memberExpression.Expression!;
        }

        if (expression != selector.Parameters[0] || segments.Count == 0)
        {
            throw new ArgumentException(
                $"A {selectorDescription} must be a property path from its model parameter.",
                paramName
            );
        }

        return segments.ToArray();
    }

    internal static string GetPropertyPath<TModel, TValue>(
        Expression<Func<TModel, TValue>> selector,
        string selectorDescription,
        string paramName
    ) => string.Join(".", GetMemberNames(selector, selectorDescription, paramName));

    internal static ConfiglueMemberPath Bind<TModel, TValue>(
        Expression<Func<TModel, TValue>> selector,
        ConfiglueModelSchema schema,
        string selectorDescription,
        string paramName
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        var names = GetMemberNames(selector, selectorDescription, paramName);
        return ConfiglueMemberPath.FromMemberNames(schema, names, paramName);
    }
}
