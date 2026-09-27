namespace Configlue;

/// <summary>Provides the generated bridge from a one-arity model to Configlue's typed runtime.</summary>
/// <typeparam name="TSelf">The generated configuration model.</typeparam>
public interface IConfiglueFacadeModel<TSelf>
    where TSelf : IConfiglueFacadeModel<TSelf>
{
    /// <summary>Gets the generated model metadata used by schema export and source registration.</summary>
    static virtual ConfiglueModelSchema GetConfiglueSchema() =>
        throw new NotSupportedException(
            $"Generated schema metadata is unavailable for facade model '{typeof(TSelf)}'."
        );

    /// <summary>Creates the model's runtime using the fragment type fixed by source generation.</summary>
    static abstract IWritableOptions<TSelf> CreateConfiglueRuntime(
        ConfiglueModelBuilder<TSelf> configuration,
        IServiceProvider? serviceProvider,
        Action<IDisposable> ownResource
    );

    /// <summary>Creates the persisted profile manager with the fragment type fixed by source generation.</summary>
    static virtual IConfiglueProfiledOptions<TSelf> CreateConfiglueProfileManager(
        IConfiglueOptionsRegistry<TSelf> registry,
        StateSource<ConfiglueProfileCatalog> catalogSource,
        string defaultProfileName
    ) =>
        throw new NotSupportedException(
            $"Generated profile support is unavailable for model '{typeof(TSelf)}'."
        );
}
