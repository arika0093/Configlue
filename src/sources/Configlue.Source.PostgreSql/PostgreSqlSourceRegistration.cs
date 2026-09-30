using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Sources;
using Npgsql;

namespace Configlue.Source.PostgreSql;

/// <summary>Options for registering a JSONB-native PostgreSQL source.</summary>
public sealed class PostgreSqlSourceOptions
{
    /// <summary>An optional stable logical source ID.</summary>
    public string? Id { get; init; }

    /// <summary>The namespace separating this source's rows from other sources in the table.</summary>
    public required string ResourceNamespace { get; init; }

    /// <summary>A caller-owned data source used for every route.</summary>
    public NpgsqlDataSource? DataSource { get; init; }

    /// <summary>Creates or resolves a caller-owned data source when a source context is materialized.</summary>
    public Func<IServiceProvider?, NpgsqlDataSource>? DataSourceFactory { get; init; }

    /// <summary>Resolves a caller-owned, shared data source for each physical route.</summary>
    public Func<IServiceProvider?, RouteKey, NpgsqlDataSource>? DataSourceResolver { get; init; }

    /// <summary>Table, schema, and resource identity settings.</summary>
    public PostgreSqlTableOptions? TableOptions { get; init; }

    /// <summary>
    /// JSON serialization options. Generated fragment converters are used automatically when registered;
    /// supply a source-generated resolver here for NativeAOT.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer.</summary>
    public bool Writable { get; init; } = true;

    internal static void Validate(PostgreSqlSourceOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ResourceNamespace);
        if (options.ResourceNamespace.Contains('\0'))
        {
            throw new ArgumentException(
                "A PostgreSQL text namespace cannot contain NUL.",
                nameof(options)
            );
        }

        options.TableOptions?.Validate();
        if (
            (options.DataSource is null ? 0 : 1)
                + (options.DataSourceFactory is null ? 0 : 1)
                + (options.DataSourceResolver is null ? 0 : 1)
            != 1
        )
        {
            throw new ArgumentException(
                "Configure exactly one of DataSource, DataSourceFactory, or DataSourceResolver.",
                nameof(options)
            );
        }

        if (options.Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        }
    }
}

/// <summary>Registers JSONB-native PostgreSQL sources.</summary>
public static class PostgreSqlSourceRegistration
{
    /// <summary>Adds a PostgreSQL source. Supplied data sources remain externally owned.</summary>
    public static ConfiglueSourceRegistration FromPostgreSql(
        this ConfiglueSourceSetBuilder sources,
        PostgreSqlSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        PostgreSqlSourceOptions.Validate(options);

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new PostgreSqlSourceDefinition(options)
        );
    }

    private sealed class PostgreSqlSourceDefinition(PostgreSqlSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var fixedDataSource = options.DataSource;
            if (options.DataSourceFactory is { } dataSourceFactory)
            {
                fixedDataSource =
                    dataSourceFactory(context.Services)
                    ?? throw new InvalidOperationException(
                        "The PostgreSQL data source factory returned null."
                    );
            }

            var serializer = new JsonStateValueSerializer<TFragment>(
                options.SerializerOptions,
                ConfiglueJsonFragmentRegistry<TFragment>.TryGetConverter(out var converter)
                    ? converter
                    : null
            );
            var source = options.DataSourceResolver is { } dataSourceResolver
                ? new PostgreSqlSource<TFragment>(
                    route =>
                        dataSourceResolver(context.Services, route)
                        ?? throw new InvalidOperationException(
                            "The PostgreSQL data source resolver returned null."
                        ),
                    options.ResourceNamespace,
                    serializer,
                    options.TableOptions,
                    options.Writable
                )
                : new PostgreSqlSource<TFragment>(
                    fixedDataSource!,
                    options.ResourceNamespace,
                    serializer,
                    options.TableOptions,
                    options.Writable
                );
            context.Own(source);

            var physicalOrigin = $"postgresql:{options.ResourceNamespace}";
            var stateSource = options.Id is { } id
                ? new StateSource<TFragment>(
                    id,
                    source,
                    options.Priority,
                    options.FallbackCondition,
                    physicalOrigin: physicalOrigin,
                    resourceId: options.TableOptions?.ResourceId
                )
                : new StateSource<TFragment>(
                    source,
                    options.Priority,
                    options.FallbackCondition,
                    physicalOrigin: physicalOrigin,
                    resourceId: options.TableOptions?.ResourceId
                );
            return context.Complete(stateSource);
        }
    }
}
