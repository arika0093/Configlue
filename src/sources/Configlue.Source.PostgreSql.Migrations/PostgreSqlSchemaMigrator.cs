#pragma warning disable S2077 // SQL identifiers are validated and quoted.

using Configlue.Source.PostgreSql;
using Npgsql;

namespace Configlue.Source.PostgreSql.Migrations;

/// <summary>
/// Creates and upgrades the Configlue PostgreSQL database schema.
/// </summary>
/// <remarks>
/// This migrator owns all DDL. Applications may run it with a principal that has schema privileges while
/// using a separate DML-only principal for <see cref="PostgreSqlSource{T}"/>. Schema versions are tracked
/// per component so future optional components can evolve their own version tracks.
/// </remarks>
public sealed class PostgreSqlSchemaMigrator
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlTableOptions _tableOptions;
    private readonly string _qualifiedTable;
    private readonly string _qualifiedComponentsTable;

    /// <summary>Creates a migrator for the supplied data source.</summary>
    public PostgreSqlSchemaMigrator(
        NpgsqlDataSource dataSource,
        PostgreSqlMigrationOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _tableOptions = options?.TableOptions ?? new PostgreSqlTableOptions();
        _tableOptions.Validate();
        _qualifiedTable =
            $"{PostgreSqlTableOptions.Quote(_tableOptions.SchemaName)}.{PostgreSqlTableOptions.Quote(_tableOptions.TableName)}";
        _qualifiedComponentsTable =
            $"{PostgreSqlTableOptions.Quote(_tableOptions.SchemaName)}.{PostgreSqlTableOptions.Quote(_tableOptions.ComponentsTableName)}";
    }

    /// <summary>Inspects the observed schema status without changing the database.</summary>
    public async ValueTask<PostgreSqlSchemaStatus> InspectAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var components = await ReadComponentsAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        return new PostgreSqlSchemaStatus(components is not null, components ?? []);
    }

    /// <summary>Creates any missing schema objects and advances component versions.</summary>
    public async ValueTask<PostgreSqlSchemaMigrationResult> MigrateAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var existing = await ReadComponentsAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var changed = existing is null;
        if (!string.Equals(_tableOptions.SchemaName, "public", StringComparison.Ordinal))
        {
            await ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                    CREATE SCHEMA IF NOT EXISTS {PostgreSqlTableOptions.Quote(
                        _tableOptions.SchemaName
                    )}
                    """,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        await ExecuteAsync(
                connection,
                transaction,
                $"""
                CREATE TABLE IF NOT EXISTS {_qualifiedComponentsTable} (
                    "component" text NOT NULL,
                    "version" integer NOT NULL CHECK ("version" > 0),
                    PRIMARY KEY ("component")
                )
                """,
                cancellationToken
            )
            .ConfigureAwait(false);

        changed |= await ApplyCoreAsync(connection, transaction, existing, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var components =
            await ReadComponentsAsync(connection, cancellationToken).ConfigureAwait(false) ?? [];
        return new PostgreSqlSchemaMigrationResult(changed, components);
    }

    private async ValueTask<bool> ApplyCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<PostgreSqlSchemaComponentVersion>? existing,
        CancellationToken cancellationToken
    )
    {
        var changed =
            existing is null
            || existing
                .Where(static component =>
                    string.Equals(
                        component.Component,
                        PostgreSqlSchemaVersions.CoreComponent,
                        StringComparison.Ordinal
                    )
                )
                .All(static component => component.Version < PostgreSqlSchemaVersions.CoreVersion);
        await ExecuteAsync(
                connection,
                transaction,
                $"""
                CREATE TABLE IF NOT EXISTS {_qualifiedTable} (
                    "model_id" text NOT NULL,
                    "resource_namespace" text NOT NULL,
                    "subject_key" text NOT NULL,
                    "payload" jsonb NOT NULL,
                    "revision" bigint NOT NULL CHECK ("revision" > 0),
                    "schema_version" integer NOT NULL CHECK ("schema_version" > 0),
                    "updated_at" timestamptz NOT NULL,
                    PRIMARY KEY ("model_id", "resource_namespace", "subject_key")
                )
                """,
                cancellationToken
            )
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                $"""
                CREATE INDEX IF NOT EXISTS {PostgreSqlTableOptions.Quote(
                    _tableOptions.TableName + "_namespace_idx"
                )}
                    ON {_qualifiedTable} ("model_id", "resource_namespace")
                """,
                cancellationToken
            )
            .ConfigureAwait(false);

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = $"""
                INSERT INTO {_qualifiedComponentsTable} ("component", "version")
                VALUES (@component, @version)
                ON CONFLICT ("component") DO UPDATE
                    SET "version" = GREATEST({_qualifiedComponentsTable}."version", EXCLUDED."version")
                """;
            upsert.Parameters.AddWithValue(
                "component",
                NpgsqlTypes.NpgsqlDbType.Text,
                PostgreSqlSchemaVersions.CoreComponent
            );
            upsert.Parameters.AddWithValue(
                "version",
                NpgsqlTypes.NpgsqlDbType.Integer,
                PostgreSqlSchemaVersions.CoreVersion
            );
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private async ValueTask<IReadOnlyList<PostgreSqlSchemaComponentVersion>?> ReadComponentsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    ) =>
        await ReadComponentsAsync(connection, transaction: null, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<IReadOnlyList<PostgreSqlSchemaComponentVersion>?> ReadComponentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        await using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = "SELECT to_regclass(@components_table)::text";
            probe.Parameters.AddWithValue(
                "components_table",
                NpgsqlTypes.NpgsqlDbType.Text,
                _qualifiedComponentsTable
            );
            var exists = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (exists is null or DBNull)
            {
                return null;
            }
        }

        var components = new List<PostgreSqlSchemaComponentVersion>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT \"component\", \"version\" FROM {_qualifiedComponentsTable} ORDER BY \"component\"";
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            components.Add(
                new PostgreSqlSchemaComponentVersion(reader.GetString(0), reader.GetInt32(1))
            );
        }

        return components;
    }

    private static async ValueTask ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandText,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
