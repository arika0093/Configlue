using System.Text.Json;
using Configlue;

namespace Configlue.Source.Environment;

/// <summary>Options for a facade environment variable source.</summary>
public sealed class EnvironmentSourceOptions
{
    /// <summary>An optional stable logical source ID used for explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The environment variable prefix.</summary>
    public required string Prefix { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Optional environment variable provider, useful for tests or custom hosts.</summary>
    public Func<IEnumerable<KeyValuePair<string, string?>>>? EnvironmentVariables { get; init; }

    /// <summary>Optional scalar conversion override. Types it declines fall back to JSON.</summary>
    public Func<string, Type, object?>? ValueParser { get; init; }

    /// <summary>JSON options used for members without a scalar conversion, such as collections.</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; init; }
}

/// <summary>Registers environment variable sources through the one-arity facade.</summary>
public static class EnvironmentFacadeSourceRegistration
{
    /// <summary>Adds a sparse, read-only environment source.</summary>
    public static ConfiglueSourceRegistration FromEnvironment(
        this ConfiglueSourceSetBuilder sources,
        EnvironmentSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Prefix);
        return sources.Add(new Definition(options));
    }

    private sealed class Definition(EnvironmentSourceOptions options) : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            return context.Complete(CreateSourceCore<TFragment>(context.ModelSchema));
        }

        private StateSource<TFragment> CreateSourceCore<TFragment>(ConfiglueModelSchema modelSchema)
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(modelSchema);
            var reader = new EnvironmentStateReader<TFragment>(
                modelSchema,
                options.Prefix,
                options.EnvironmentVariables,
                options.ValueParser,
                options.JsonSerializerOptions
            );
            var physicalOrigin =
                $"environment:{EnvironmentStateReader<TFragment>.NormalizePrefix(options.Prefix)}";
            return options.Id is { } id
                ? new StateSource<TFragment>(
                    id,
                    reader,
                    options.Priority,
                    options.FallbackCondition,
                    physicalOrigin: physicalOrigin
                )
                : new StateSource<TFragment>(
                    reader,
                    options.Priority,
                    options.FallbackCondition,
                    physicalOrigin: physicalOrigin,
                    logicalDescriptor: "environment-prefix"
                );
        }
    }
}
