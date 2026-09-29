namespace Configlue.CompilerServices;

/// <summary>A closed generated model registration, consumed by Core without reflection.</summary>
/// <typeparam name="TModel">The generated model.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class ConfiglueModelDescriptor<TModel>
    where TModel : IConfiglueFacadeModel<TModel>
{
    internal ConfiglueModelDescriptor(
        ConfiglueModelSchema schema,
        Func<
            ConfiglueModelBuilder<TModel>,
            IServiceProvider?,
            Action<IDisposable>,
            IWritableState<TModel>
        > createRuntime,
        Func<
            IConfiglueStateRegistry<TModel>,
            StateSource<ConfiglueProfileCatalog>,
            string,
            IConfiglueProfiledState<TModel>
        > createProfiles
    )
    {
        Schema = schema;
        CreateRuntime = createRuntime;
        CreateProfiles = createProfiles;
    }

    /// <summary>Generated model schema.</summary>
    public ConfiglueModelSchema Schema { get; }

    /// <summary>The factory with closed model and fragment types.</summary>
    public Func<
        ConfiglueModelBuilder<TModel>,
        IServiceProvider?,
        Action<IDisposable>,
        IWritableState<TModel>
    > CreateRuntime { get; }

    /// <summary>The closed factory for persisted profiles.</summary>
    public Func<
        IConfiglueStateRegistry<TModel>,
        StateSource<ConfiglueProfileCatalog>,
        string,
        IConfiglueProfiledState<TModel>
    > CreateProfiles { get; }
}

/// <summary>The compact runtime bridge used only by generated model descriptors.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class ConfiglueRuntime
{
    /// <summary>Describes a generated model with its statically closed fragment operations.</summary>
    public static ConfiglueModelDescriptor<TModel> Describe<TModel, TFragment>(
        ConfiglueModelSchema schema
    )
        where TModel : IConfiglueFacadeModel<TModel>, IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new ConfiglueModelDescriptor<TModel>(
            schema,
            (configuration, services, ownResource) =>
                new ConfiglueRuntime<TModel, TFragment>(
                    configuration.BuildSources<TFragment>(schema, services, ownResource),
                    configuration.WriteRoute,
                    configuration.WritePlan,
                    configuration.GetMigrations<TFragment>(services),
                    configuration.GetValidators(services),
                    configuration.ValidateDataAnnotations,
                    configuration.OnChangeDebounce,
                    configuration.StateName,
                    configuration.GetLogger(services),
                    configuration.CloneStrategy,
                    configuration.ReadValidationMode,
                    configuration.WriteConflictResolution,
                    configuration.RouteSelector
                ),
            static (registry, catalog, name) =>
                new ConfiglueProfiledState<TModel, TFragment>(registry, catalog, name)
        );
    }
}
