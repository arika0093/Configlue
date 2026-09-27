using Configlue;

namespace Configlue.Source.Environment;

/// <summary>Options for a facade environment variable source.</summary>
public sealed class EnvironmentSourceOptions
{
    /// <summary>The stable logical source ID.</summary>
    public required string Id { get; init; }

    /// <summary>The environment variable prefix.</summary>
    public required string Prefix { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Optional environment variable provider, useful for tests or custom hosts.</summary>
    public Func<IEnumerable<KeyValuePair<string, string?>>>? EnvironmentVariables { get; init; }

    /// <summary>Optional scalar conversion override.</summary>
    public Func<string, Type, object?>? ValueParser { get; init; }
}

/// <summary>Registers environment variable sources through the one-arity facade.</summary>
public static class EnvironmentFacadeSourceRegistration
{
    /// <summary>Adds a sparse, read-only environment source.</summary>
    public static void FromEnvironment(
        this ConfiglueSourceSetBuilder sources,
        EnvironmentSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Prefix);
        sources.Add(new Definition(options));
    }

    private sealed class Definition(EnvironmentSourceOptions options) : IConfiglueSourceDefinition
    {
        public StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema modelSchema,
            IServiceProvider? serviceProvider,
            Action<IDisposable> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(modelSchema);
            return new StateSource<TFragment>(
                options.Id,
                new EnvironmentStateReader<TFragment>(
                    modelSchema,
                    options.Prefix,
                    options.EnvironmentVariables,
                    options.ValueParser
                ),
                options.Priority,
                options.FallbackCondition,
                physicalOrigin: $"environment:{EnvironmentStateReader<TFragment>.NormalizePrefix(options.Prefix)}"
            );
        }
    }
}
