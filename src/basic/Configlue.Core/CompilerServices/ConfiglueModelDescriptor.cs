using System.Diagnostics.CodeAnalysis;

namespace Configlue.CompilerServices;

/// <summary>A closed generated model registration, consumed by Core without reflection.</summary>
/// <typeparam name="TModel">The generated model.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class ConfiglueModelDescriptor<TModel>
    where TModel : IConfiglueFacadeModel<TModel>
{
    private static ConfiglueModelDescriptor<TModel>? _current;

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2059",
        Justification = "The handle comes from typeof(TModel), a generated model already rooted by this closed generic instantiation. Running its static constructor only triggers generated registration."
    )]
    private static void EnsureRegistered()
    {
        // The generated model registers its descriptor from its own type initializer.
        // Running the model's class constructor here keeps registration reflection-free
        // and AOT-friendly without relying on a module initializer (unsupported by Unity).
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(TModel).TypeHandle
        );
    }

    internal ConfiglueModelDescriptor(
        ConfiglueModelSchema schema,
        Func<
            ConfiglueModelBuilder<TModel>,
            IServiceProvider?,
            Action<object>,
            IWritableState<TModel>
        > createRuntime,
        Func<
            IConfiglueStateRegistry<TModel>,
            StateSource<ConfiglueProfileCatalog>,
            string,
            IConfiglueProfiledState<TModel>
        > createProfiles,
        Type fragmentType,
        Func<object, object> toFragmentBoxed,
        Func<object, object> fromFragmentBoxed
    )
    {
        Schema = schema;
        CreateRuntime = createRuntime;
        CreateProfiles = createProfiles;
        FragmentType = fragmentType;
        ToFragmentBoxed = toFragmentBoxed;
        FromFragmentBoxed = fromFragmentBoxed;
    }

    /// <summary>Generated model schema.</summary>
    public ConfiglueModelSchema Schema { get; }

    /// <summary>The generated fragment CLR type for this model.</summary>
    public Type FragmentType { get; }

    /// <summary>Converts a boxed model value to its boxed complete fragment.</summary>
    public Func<object, object> ToFragmentBoxed { get; }

    /// <summary>Converts a boxed complete fragment to its boxed model value.</summary>
    public Func<object, object> FromFragmentBoxed { get; }

    /// <summary>Gets the descriptor registered by generated model code.</summary>
    public static ConfiglueModelDescriptor<TModel> Current
    {
        get
        {
            var current = Volatile.Read(ref _current);
            if (current is not null)
            {
                return current;
            }

            // Initialize from the getter rather than a registry type initializer.
            // Registration can then enter this type while another thread waits for
            // the model initializer, without a circular class-initialization lock.
            EnsureRegistered();
            return Volatile.Read(ref _current)
                ?? throw new InvalidOperationException(
                    $"Generated runtime descriptor for model '{typeof(TModel)}' has not been registered."
                );
        }
    }

    /// <summary>Registers the generated runtime descriptor for its model.</summary>
    public static void Register(ConfiglueModelDescriptor<TModel> descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (Interlocked.CompareExchange(ref _current, descriptor, null) is not null)
        {
            throw new InvalidOperationException(
                $"Generated runtime descriptor for model '{typeof(TModel)}' is already registered."
            );
        }
    }

    /// <summary>The factory with closed model and fragment types.</summary>
    public Func<
        ConfiglueModelBuilder<TModel>,
        IServiceProvider?,
        Action<object>,
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
        ConfiglueModelOperations<TModel, TFragment> operations
    )
        where TModel : IConfiglueFacadeModel<TModel>, IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(operations);
        ConfiglueModelOperations<TModel, TFragment>.Register(operations);
        var schema = operations.Schema;
        Func<object, object> toFragmentBoxed = model => operations.ToFragment((TModel)model);
        Func<object, object> fromFragmentBoxed = fragment =>
            operations.FromFragment((TFragment)fragment);
        return new ConfiglueModelDescriptor<TModel>(
            schema,
            (configuration, services, ownResource) =>
                new ConfiglueRuntime<TModel, TFragment>(
                    configuration.BuildSources<TFragment>(schema, services, ownResource),
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
                    configuration.Diagnostics,
                    migrationSources: configuration.BuildMigrationSources<TFragment>(
                        schema,
                        services,
                        ownResource
                    )
                ),
            static (registry, catalog, name) =>
                new ConfiglueProfiledState<TModel, TFragment>(registry, catalog, name),
            typeof(TFragment),
            toFragmentBoxed,
            fromFragmentBoxed
        );
    }
}
