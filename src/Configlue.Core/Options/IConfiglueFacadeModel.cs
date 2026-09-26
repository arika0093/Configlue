namespace Configlue;

/// <summary>Provides the generated bridge from a one-arity model to Configlue's typed runtime.</summary>
/// <typeparam name="TSelf">The generated configuration model.</typeparam>
public interface IConfiglueFacadeModel<TSelf>
    where TSelf : IConfiglueFacadeModel<TSelf>
{
    /// <summary>Creates the model's runtime using the fragment type fixed by source generation.</summary>
    static abstract IWritableOptions<TSelf> CreateConfiglueRuntime(
        ConfiglueModelBuilder<TSelf> configuration,
        IServiceProvider? serviceProvider
    );
}
