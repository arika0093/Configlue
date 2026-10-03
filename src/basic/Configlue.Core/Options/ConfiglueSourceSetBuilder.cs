using Configlue.CompilerServices;
using Configlue.Extensibility;
using Configlue.Resources;

namespace Configlue;

/// <summary>Collects typed state sources without requiring a Fragment type argument on the model API.</summary>
public class ConfiglueSourceSetBuilder : IConfiglueSourceRegistrationSink
{
    private readonly List<IConfiglueSourceRegistration> _sources = [];
    private bool _sealed;

    /// <summary>Whether this source set has been built or its model registration has been sealed.</summary>
    public bool IsSealed => _sealed;

    /// <summary>Adds an already-created source. Its resource instances remain caller-owned.</summary>
    public void Add<TFragment>(StateSource<TFragment> source)
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(source);
        _sources.Add(
            new ConfiglueSourceRegistration<TFragment>(_ => source, source.RuntimeLifetime)
        );
    }

    /// <summary>Adds a source factory. The service provider is null in a non-DI context.</summary>
    public void Add<TFragment>(Func<IServiceProvider?, StateSource<TFragment>> sourceFactory)
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(sourceFactory);
        _sources.Add(new ConfiglueSourceRegistration<TFragment>(sourceFactory));
    }

    void IConfiglueSourceRegistrationSink.Add<TModel, TFragment>(
        SourceKey<TModel> sourceKey,
        StateSource<TFragment> source
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        AddKeyed(sourceKey, _ => source);
    }

    void IConfiglueSourceRegistrationSink.Add<TModel, TFragment>(
        SourceKey<TModel> sourceKey,
        Func<IServiceProvider?, StateSource<TFragment>> sourceFactory
    ) => AddKeyed(sourceKey, sourceFactory);

    ConfiglueSourceRegistration IConfiglueSourceRegistrationSink.Add(
        IConfiglueSourceDefinition definition
    )
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(definition);
        var options = new ConfiglueSourceRegistration(EnsureMutable);
        _sources.Add(new ConfiglueSourceDefinitionRegistration(definition, options));
        return options;
    }

    private void AddKeyed<TModel, TFragment>(
        SourceKey<TModel> sourceKey,
        Func<IServiceProvider?, StateSource<TFragment>> sourceFactory
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(sourceFactory);
        if (sourceKey.IsDefault)
        {
            throw new ArgumentException("The source key is uninitialized.", nameof(sourceKey));
        }

        _sources.Add(
            new ConfiglueSourceRegistration<TFragment>(provider =>
            {
                var source =
                    sourceFactory(provider)
                    ?? throw new InvalidOperationException("A source factory returned null.");
                var keyedSource = new StateSource<TFragment>(
                    sourceKey.Id,
                    source.Reader,
                    new StateSourceOptions<TFragment>
                    {
                        Priority = source.Priority,
                        FallbackCondition = source.FallbackCondition,
                        Writer = source.Writer,
                        DisableWriteCapability = source.Writer is null,
                        Watcher = source.Watcher,
                        PhysicalOrigin = source.PhysicalOrigin,
                        FixedResourceId = source.ConfiguredResourceId,
                        ResourceKeySelector = source.GetResourceKey,
                        RouteSelector = source.GetRouteKey,
                    }
                );
                source.CopyRoutingMetadataTo(keyedSource);
                return keyedSource;
            })
        );
    }

    internal StateSourceSet<TFragment> Build<TFragment>(IServiceProvider? serviceProvider)
        where TFragment : class, IConfiglueFragment<TFragment> =>
        Build<TFragment>(default!, serviceProvider, static _ => { });

    internal StateSourceSet<TFragment> Build<TFragment>(
        ConfiglueModelSchema? modelSchema,
        IServiceProvider? serviceProvider,
        Action<object> ownResource,
        IConfiglueHostPaths? hostPaths = null
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        Seal();
        if (modelSchema is null && _sources.Any(static source => source.RequiresGeneratedModel))
        {
            throw new InvalidOperationException(
                "Provider source definitions require the generated facade to supply model metadata and resource ownership."
            );
        }

        var sources = new List<StateSource<TFragment>>(_sources.Count);
        foreach (var registration in _sources)
        {
            var source = registration.Create<TFragment>(
                modelSchema,
                serviceProvider,
                ownResource,
                hostPaths ?? ConfiglueHostPathProfile.Default
            );
            sources.Add(source.WithModelId(modelSchema?.Id ?? source.ModelId));
        }

        return new StateSourceSet<TFragment>(sources);
    }

    internal void CopyFrom(ConfiglueSourceSetBuilder source)
    {
        if (_sealed)
        {
            throw new InvalidOperationException("The source registration has already been added.");
        }
        _sources.AddRange(source._sources);
    }

    /// <summary>The combined lifetime requirement declared by the registered sources.</summary>
    internal RuntimeLifetimeRequirement DeclaredRuntimeLifetime =>
        _sources.Select(static source => source.RuntimeLifetime).Combine();

    internal void Seal() => _sealed = true;

    private void EnsureMutable()
    {
        if (_sealed)
        {
            throw new InvalidOperationException("The source registration has already been added.");
        }
    }

    private interface IConfiglueSourceRegistration
    {
        bool RequiresGeneratedModel { get; }

        RuntimeLifetimeRequirement RuntimeLifetime { get; }

        StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema? modelSchema,
            IServiceProvider? serviceProvider,
            Action<object> ownResource,
            IConfiglueHostPaths hostPaths
        )
            where TFragment : class, IConfiglueFragment<TFragment>;
    }

    private sealed class ConfiglueSourceRegistration<TFragment>(
        Func<IServiceProvider?, StateSource<TFragment>> sourceFactory,
        RuntimeLifetimeRequirement runtimeLifetime = RuntimeLifetimeRequirement.Shared
    ) : IConfiglueSourceRegistration
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        public bool RequiresGeneratedModel => false;

        public RuntimeLifetimeRequirement RuntimeLifetime { get; } = runtimeLifetime;

        public StateSource<TRequestedFragment> Create<TRequestedFragment>(
            ConfiglueModelSchema? modelSchema,
            IServiceProvider? serviceProvider,
            Action<object> ownResource,
            IConfiglueHostPaths hostPaths
        )
            where TRequestedFragment : class, IConfiglueFragment<TRequestedFragment>
        {
            if (typeof(TFragment) != typeof(TRequestedFragment))
            {
                throw new InvalidOperationException(
                    $"A source for model fragment '{typeof(TFragment)}' cannot be used with '{typeof(TRequestedFragment)}'."
                );
            }

            return (StateSource<TRequestedFragment>)
                (object)(
                    sourceFactory(serviceProvider)
                    ?? throw new InvalidOperationException("A source factory returned null.")
                );
        }
    }

    private sealed class ConfiglueSourceDefinitionRegistration(
        IConfiglueSourceDefinition definition,
        ConfiglueSourceRegistration options
    ) : IConfiglueSourceRegistration
    {
        public bool RequiresGeneratedModel => true;

        public RuntimeLifetimeRequirement RuntimeLifetime =>
            options.RuntimeLifetimeOverride
            ?? (definition as IConfiglueRuntimeLifetimeSource)?.RuntimeLifetime
            ?? RuntimeLifetimeRequirement.Shared;

        public StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema? modelSchema,
            IServiceProvider? serviceProvider,
            Action<object> ownResource,
            IConfiglueHostPaths hostPaths
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            var context = new ConfiglueSourceCreationContext(
                modelSchema
                    ?? throw new InvalidOperationException(
                        "Provider source definitions require generated model metadata."
                    ),
                serviceProvider,
                hostPaths
            );
            try
            {
                var result =
                    definition.Create<TFragment>(context)
                    ?? throw new InvalidOperationException("A source definition returned null.");
                foreach (var resource in result.OwnedResources)
                {
                    ownResource(resource);
                }
                return options.Apply(result.Source);
            }
            finally
            {
                // Include allocations made before a provider throws, so creation failure cannot leak resources.
                foreach (var resource in context.CreatedResources)
                {
                    ownResource(resource);
                }
            }
        }
    }
}

/// <summary>Collects sources for one generated model and enables strongly typed provider registration.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
public sealed class ConfiglueSourceSetBuilder<TModel> : ConfiglueSourceSetBuilder
    where TModel : IConfiglueFacadeModel<TModel>
{
    /// <summary>The generated model associated with this source set.</summary>
    public Type ModelType => typeof(TModel);
}
