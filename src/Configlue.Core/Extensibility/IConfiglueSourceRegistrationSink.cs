namespace Configlue.Extensibility;

/// <summary>Low-level source registration port implemented by root source-set builders for provider composition.</summary>
/// <remarks>
/// Application code normally registers sources through the higher-level
/// <see cref="ConfiglueSourceSetBuilder"/> API or provider-specific fluent extensions. Provider and
/// composition code uses this port to register generated-model sources and provider source definitions
/// without widening the application-facing builder surface.
/// </remarks>
public interface IConfiglueSourceRegistrationSink
{
    /// <summary>Adds a provider-defined source using the generated model's fragment type.</summary>
    /// <param name="definition">The provider source definition.</param>
    /// <returns>Registration options that apply routing and capability settings to the created source.</returns>
    ConfiglueSourceRegistration Add(IConfiglueSourceDefinition definition);

    /// <summary>Adds a provider-created source under a typed key.</summary>
    /// <typeparam name="TModel">The configuration model type.</typeparam>
    /// <typeparam name="TFragment">The generated sparse state fragment.</typeparam>
    /// <param name="sourceKey">The typed key assigned to the source.</param>
    /// <param name="source">The already-created source.</param>
    void Add<TModel, TFragment>(SourceKey<TModel> sourceKey, StateSource<TFragment> source)
        where TFragment : class, IConfiglueFragment<TFragment>;

    /// <summary>Adds a provider-aware source factory under a typed key.</summary>
    /// <typeparam name="TModel">The configuration model type.</typeparam>
    /// <typeparam name="TFragment">The generated sparse state fragment.</typeparam>
    /// <param name="sourceKey">The typed key assigned to the source.</param>
    /// <param name="sourceFactory">Creates the source on demand using the current service provider.</param>
    void Add<TModel, TFragment>(
        SourceKey<TModel> sourceKey,
        Func<IServiceProvider?, StateSource<TFragment>> sourceFactory
    )
        where TFragment : class, IConfiglueFragment<TFragment>;
}
