namespace Configlue.CompilerServices;

/// <summary>Generated, statically typed operations for one model and fragment pair.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class ConfiglueModelOperations<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private static ConfiglueModelOperations<TModel, TFragment>? _current;

    /// <summary>Creates a generated operation table.</summary>
    public ConfiglueModelOperations(
        ConfiglueModelSchema schema,
        TFragment emptyFragment,
        Func<TModel, TFragment> toFragment,
        Func<TModel, TModel, TFragment> diff,
        Func<TFragment, TModel> fromFragment
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(emptyFragment);
        ArgumentNullException.ThrowIfNull(toFragment);
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(fromFragment);
        Schema = schema;
        EmptyFragment = emptyFragment;
        ToFragment = toFragment;
        Diff = diff;
        FromFragment = fromFragment;
    }

    /// <summary>Gets the model operations registered by generated code.</summary>
    public static ConfiglueModelOperations<TModel, TFragment> Current =>
        _current
        ?? throw new InvalidOperationException(
            $"Generated operations for model '{typeof(TModel)}' have not been registered."
        );

    /// <summary>The generated model schema.</summary>
    public ConfiglueModelSchema Schema { get; }

    /// <summary>An empty fragment.</summary>
    public TFragment EmptyFragment { get; }

    /// <summary>Creates a complete fragment from a model.</summary>
    public Func<TModel, TFragment> ToFragment { get; }

    /// <summary>Creates a sparse semantic diff.</summary>
    public Func<TModel, TModel, TFragment> Diff { get; }

    /// <summary>Creates a model from a merged fragment.</summary>
    public Func<TFragment, TModel> FromFragment { get; }

    /// <summary>Registers the generated operation table for its closed model and fragment types.</summary>
    public static void Register(ConfiglueModelOperations<TModel, TFragment> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (Interlocked.CompareExchange(ref _current, operations, null) is not null)
        {
            throw new InvalidOperationException(
                $"Generated operations for model '{typeof(TModel)}' are already registered."
            );
        }

        ConfiglueModelSchemaRegistry<TModel>.Register(operations.Schema);
        ConfiglueModelSchemaCatalog.Register(typeof(TModel), operations.Schema);
        ConfiglueFragmentRegistry<TFragment>.Register(operations.EmptyFragment);
    }
}

/// <summary>Provides a generated empty fragment without static interface dispatch.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class ConfiglueFragmentRegistry<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private static TFragment? _empty;

    /// <summary>Gets the generated empty fragment registered for this fragment type.</summary>
    public static TFragment Empty =>
        _empty
        ?? throw new InvalidOperationException(
            $"Generated fragment '{typeof(TFragment)}' has not been registered."
        );

    /// <summary>Registers the generated empty fragment.</summary>
    public static void Register(TFragment empty)
    {
        ArgumentNullException.ThrowIfNull(empty);
        if (Interlocked.CompareExchange(ref _empty, empty, null) is not null)
        {
            throw new InvalidOperationException(
                $"Generated fragment '{typeof(TFragment)}' is already registered."
            );
        }
    }
}

/// <summary>Provides the generated schema for one model without static interface dispatch.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class ConfiglueModelSchemaRegistry<TModel>
    where TModel : IConfiglueModel
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "SonarAnalyzer.CSharp",
        "S2743",
        Justification = "The closed generic type is the per-model registry key."
    )]
    private static ConfiglueModelSchema? _schema;

    /// <summary>Gets the generated schema registered for this model.</summary>
    public static ConfiglueModelSchema Schema =>
        _schema
        ?? throw new InvalidOperationException(
            $"Generated schema for model '{typeof(TModel)}' has not been registered."
        );

    /// <summary>Registers the generated schema for this model.</summary>
    public static void Register(ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (Interlocked.CompareExchange(ref _schema, schema, null) is not null)
        {
            throw new InvalidOperationException(
                $"Generated schema for model '{typeof(TModel)}' is already registered."
            );
        }
    }
}
