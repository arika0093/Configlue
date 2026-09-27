using System.Linq.Expressions;

namespace Configlue;

/// <summary>Mounts source-specific generated fragments into a root model's nested member.</summary>
public static class ConfiglueSourceSetBuilderMountExtensions
{
    /// <summary>Adds a nested source using a generated model member selector.</summary>
    /// <remarks>The source remains caller-owned. The generated fragment path is used as the reverse mapping when the source supports writes.</remarks>
    public static void AddMounted<TModel, TRootFragment, TSubtreeModel, TSubtreeFragment>(
        this ConfiglueSourceSetBuilder sources,
        StateSource<TSubtreeFragment> source,
        Expression<Func<TModel, TSubtreeModel?>> subtreeSelector
    )
        where TModel : IConfiglueModel<TModel, TRootFragment>
        where TRootFragment : class, IConfiglueFragment<TRootFragment>
        where TSubtreeModel : class, IConfiglueModel<TSubtreeModel, TSubtreeFragment>
        where TSubtreeFragment : class, IConfiglueFragment<TSubtreeFragment>
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(subtreeSelector);
        var propertyPath = GetPropertyPath(subtreeSelector);
        AddMounted<TRootFragment, TSubtreeFragment>(
            sources,
            _ => source,
            propertyPath,
            root => ExtractMountedFragment<TRootFragment, TSubtreeFragment>(root, propertyPath)
        );
    }

    /// <summary>Adds a writable nested source using a generated model member selector.</summary>
    public static void AddMounted<TModel, TRootFragment, TSubtreeModel, TSubtreeFragment>(
        this ConfiglueSourceSetBuilder sources,
        StateSource<TSubtreeFragment> source,
        Expression<Func<TModel, TSubtreeModel?>> subtreeSelector,
        Func<TRootFragment, TSubtreeFragment> toSource
    )
        where TModel : IConfiglueModel<TModel, TRootFragment>
        where TRootFragment : class, IConfiglueFragment<TRootFragment>
        where TSubtreeModel : class, IConfiglueModel<TSubtreeModel, TSubtreeFragment>
        where TSubtreeFragment : class, IConfiglueFragment<TSubtreeFragment>
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(subtreeSelector);
        ArgumentNullException.ThrowIfNull(toSource);
        var propertyPath = GetPropertyPath(subtreeSelector);
        AddMounted<TRootFragment, TSubtreeFragment>(sources, _ => source, propertyPath, toSource);
    }

    /// <summary>Adds a nested source factory using a generated model member selector.</summary>
    /// <remarks>Factory-created sources remain caller-owned. The generated fragment path is used as the reverse mapping when the source supports writes.</remarks>
    public static void AddMounted<TModel, TRootFragment, TSubtreeModel, TSubtreeFragment>(
        this ConfiglueSourceSetBuilder sources,
        Func<IServiceProvider?, StateSource<TSubtreeFragment>> sourceFactory,
        Expression<Func<TModel, TSubtreeModel?>> subtreeSelector
    )
        where TModel : IConfiglueModel<TModel, TRootFragment>
        where TRootFragment : class, IConfiglueFragment<TRootFragment>
        where TSubtreeModel : class, IConfiglueModel<TSubtreeModel, TSubtreeFragment>
        where TSubtreeFragment : class, IConfiglueFragment<TSubtreeFragment>
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        ArgumentNullException.ThrowIfNull(subtreeSelector);
        var propertyPath = GetPropertyPath(subtreeSelector);
        AddMounted<TRootFragment, TSubtreeFragment>(
            sources,
            sourceFactory,
            propertyPath,
            root => ExtractMountedFragment<TRootFragment, TSubtreeFragment>(root, propertyPath)
        );
    }

    /// <summary>Adds a writable nested source factory using a generated model member selector.</summary>
    public static void AddMounted<TModel, TRootFragment, TSubtreeModel, TSubtreeFragment>(
        this ConfiglueSourceSetBuilder sources,
        Func<IServiceProvider?, StateSource<TSubtreeFragment>> sourceFactory,
        Expression<Func<TModel, TSubtreeModel?>> subtreeSelector,
        Func<TRootFragment, TSubtreeFragment> toSource
    )
        where TModel : IConfiglueModel<TModel, TRootFragment>
        where TRootFragment : class, IConfiglueFragment<TRootFragment>
        where TSubtreeModel : class, IConfiglueModel<TSubtreeModel, TSubtreeFragment>
        where TSubtreeFragment : class, IConfiglueFragment<TSubtreeFragment>
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);
        ArgumentNullException.ThrowIfNull(subtreeSelector);
        ArgumentNullException.ThrowIfNull(toSource);
        var propertyPath = GetPropertyPath(subtreeSelector);
        AddMounted<TRootFragment, TSubtreeFragment>(sources, sourceFactory, propertyPath, toSource);
    }

    /// <summary>
    /// Adds a nested source using its generated fragment type and a validated logical property path.
    /// The source remains caller-owned. Without a reverse mapping, the mounted source is read-only.
    /// </summary>
    public static void AddMounted<TRootFragment, TSubtreeFragment>(
        this ConfiglueSourceSetBuilder sources,
        StateSource<TSubtreeFragment> source,
        string propertyPath
    )
        where TRootFragment : class, IConfiglueFragment<TRootFragment>
        where TSubtreeFragment : class, IConfiglueFragment<TSubtreeFragment>
    {
        ArgumentNullException.ThrowIfNull(source);
        AddMounted<TRootFragment, TSubtreeFragment>(sources, _ => source, propertyPath);
    }

    /// <summary>Adds a writable nested source using a generated fragment type and a validated logical property path.</summary>
    public static void AddMounted<TRootFragment, TSubtreeFragment>(
        this ConfiglueSourceSetBuilder sources,
        StateSource<TSubtreeFragment> source,
        string propertyPath,
        Func<TRootFragment, TSubtreeFragment> toSource
    )
        where TRootFragment : class, IConfiglueFragment<TRootFragment>
        where TSubtreeFragment : class, IConfiglueFragment<TSubtreeFragment>
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(toSource);
        AddMounted<TRootFragment, TSubtreeFragment>(sources, _ => source, propertyPath, toSource);
    }

    /// <summary>
    /// Adds a nested source factory using its generated fragment type and a validated logical path.
    /// The factory-created source remains caller-owned. Without a reverse mapping, the mounted source is read-only.
    /// </summary>
    public static void AddMounted<TRootFragment, TSubtreeFragment>(
        this ConfiglueSourceSetBuilder sources,
        Func<IServiceProvider?, StateSource<TSubtreeFragment>> sourceFactory,
        string propertyPath
    )
        where TRootFragment : class, IConfiglueFragment<TRootFragment>
        where TSubtreeFragment : class, IConfiglueFragment<TSubtreeFragment>
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        sources.Add(
            new MountedSourceDefinition<TRootFragment, TSubtreeFragment>(
                sourceFactory,
                propertyPath,
                null
            )
        );
    }

    /// <summary>Adds a writable nested source factory using its generated fragment type and a validated logical path.</summary>
    public static void AddMounted<TRootFragment, TSubtreeFragment>(
        this ConfiglueSourceSetBuilder sources,
        Func<IServiceProvider?, StateSource<TSubtreeFragment>> sourceFactory,
        string propertyPath,
        Func<TRootFragment, TSubtreeFragment> toSource
    )
        where TRootFragment : class, IConfiglueFragment<TRootFragment>
        where TSubtreeFragment : class, IConfiglueFragment<TSubtreeFragment>
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sourceFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        ArgumentNullException.ThrowIfNull(toSource);
        sources.Add(
            new MountedSourceDefinition<TRootFragment, TSubtreeFragment>(
                sourceFactory,
                propertyPath,
                toSource
            )
        );
    }

    private sealed class MountedSourceDefinition<TRootFragment, TSubtreeFragment>(
        Func<IServiceProvider?, StateSource<TSubtreeFragment>> sourceFactory,
        string propertyPath,
        Func<TRootFragment, TSubtreeFragment>? toSource
    ) : IConfiglueSourceDefinition
        where TRootFragment : class, IConfiglueFragment<TRootFragment>
        where TSubtreeFragment : class, IConfiglueFragment<TSubtreeFragment>
    {
        public StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema modelSchema,
            IServiceProvider? serviceProvider,
            Action<IDisposable> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            if (typeof(TFragment) != typeof(TRootFragment))
            {
                throw new InvalidOperationException(
                    $"Mounted source for root fragment '{typeof(TRootFragment)}' cannot be registered for '{typeof(TFragment)}'."
                );
            }

            var source = sourceFactory(serviceProvider);
            ArgumentNullException.ThrowIfNull(source);
            var mounted = toSource is null
                ? StateSourceProjection.Mount<TSubtreeFragment, TRootFragment>(source, propertyPath)
                : StateSourceProjection.Mount<TSubtreeFragment, TRootFragment>(
                    source,
                    propertyPath,
                    toSource
                );
            return (StateSource<TFragment>)(object)mounted;
        }
    }

    private static string GetPropertyPath<TModel, TSubtreeModel>(
        Expression<Func<TModel, TSubtreeModel?>> selector
    )
    {
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
            if (memberExpression.Member.MemberType != System.Reflection.MemberTypes.Property)
            {
                throw new ArgumentException(
                    "A mounted subtree selector must use generated model properties.",
                    nameof(selector)
                );
            }

            segments.Push(memberExpression.Member.Name);
            expression = memberExpression.Expression!;
        }

        if (expression != selector.Parameters[0] || segments.Count == 0)
        {
            throw new ArgumentException(
                "A mounted subtree selector must be a property path from its model parameter.",
                nameof(selector)
            );
        }

        return string.Join('.', segments);
    }

    private static TSubtreeFragment ExtractMountedFragment<TRootFragment, TSubtreeFragment>(
        TRootFragment root,
        string propertyPath
    )
        where TRootFragment : class, IConfiglueFragment<TRootFragment>
        where TSubtreeFragment : class, IConfiglueFragment<TSubtreeFragment> =>
        (TSubtreeFragment)StateSourceProjection.ExtractMountedFragment(root, propertyPath);
}
