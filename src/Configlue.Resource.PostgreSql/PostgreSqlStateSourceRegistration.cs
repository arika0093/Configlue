using Configlue.Sources;
using Npgsql;

namespace Configlue.Resource.PostgreSql;

/// <summary>Options for registering a PostgreSQL-backed serialized state source.</summary>
public sealed class PostgreSqlStateSourceOptions
{
    /// <summary>An optional stable logical source ID.</summary>
    public string? Id { get; init; }

    /// <summary>The namespace separating this resource's rows from other resources in the table.</summary>
    public required string ResourceNamespace { get; init; }

    /// <summary>A caller-owned data source used for every route.</summary>
    public NpgsqlDataSource? DataSource { get; init; }

    /// <summary>Creates or resolves a caller-owned data source when a source context is materialized.</summary>
    public Func<IServiceProvider?, NpgsqlDataSource>? DataSourceFactory { get; init; }

    /// <summary>Resolves a caller-owned, shared data source for each physical route.</summary>
    public Func<IServiceProvider?, RouteKey, NpgsqlDataSource>? DataSourceResolver { get; init; }

    /// <summary>The codec for the serialized state.</summary>
    public required object Codec { get; init; }

    /// <summary>Table and resource identity settings.</summary>
    public PostgreSqlResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer.</summary>
    public bool Writable { get; init; } = true;

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }
}

/// <summary>Registers sources backed by PostgreSQL byte resources.</summary>
public static class PostgreSqlStateSourceRegistration
{
    /// <summary>Adds a PostgreSQL source. Supplied data sources remain externally owned.</summary>
    public static ConfiglueSourceRegistration FromPostgreSql(
        this ConfiglueSourceSetBuilder sources,
        PostgreSqlStateSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ResourceNamespace);
        if (options.ResourceNamespace.Contains('\0'))
        {
            throw new ArgumentException(
                "A PostgreSQL text namespace cannot contain NUL.",
                nameof(options)
            );
        }

        ArgumentNullException.ThrowIfNull(options.Codec);
        options.ResourceOptions?.Validate();
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

        return sources.Add(new PostgreSqlStateSourceDefinition(options));
    }

    private sealed class PostgreSqlStateSourceDefinition(PostgreSqlStateSourceOptions options)
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

            var resource = options.DataSourceResolver is { } dataSourceResolver
                ? new PostgreSqlResource(
                    route =>
                        dataSourceResolver(context.Services, route)
                        ?? throw new InvalidOperationException(
                            "The PostgreSQL data source resolver returned null."
                        ),
                    options.ResourceNamespace,
                    options.ResourceOptions
                )
                : new PostgreSqlResource(
                    fixedDataSource!,
                    options.ResourceNamespace,
                    options.ResourceOptions
                );
            context.Own(resource);

            var reader = new SerializedStateReader<TFragment>(
                resource,
                options.Codec,
                options.CodecContext
            );
            ISourceWriter<TFragment>? writer = options.Writable
                ? new SerializedStateWriter<TFragment>(
                    resource,
                    options.Codec,
                    options.CodecContext
                )
                : null;
            var physicalOrigin = $"postgresql:{options.ResourceNamespace}";
            var source = options.Id is { } id
                ? new StateSource<TFragment>(
                    id,
                    reader,
                    options.Priority,
                    options.FallbackCondition,
                    writer,
                    physicalOrigin: physicalOrigin,
                    resourceId: options.ResourceOptions?.ResourceId
                )
                : new StateSource<TFragment>(
                    reader,
                    options.Priority,
                    options.FallbackCondition,
                    writer,
                    physicalOrigin: physicalOrigin,
                    resourceId: options.ResourceOptions?.ResourceId
                );
            return context.Complete(source);
        }
    }
}
