using Configlue.Source.PostgreSql;
using Configlue.Source.PostgreSql.Migrations;
using Npgsql;

namespace Configlue.Tests;

public sealed class PostgreSqlSchemaGuardTests
{
    [Test]
    public void EnsureSchemaCompatible_ThrowsWhenTheStateTableIsMissing()
    {
        var exception = Should.Throw<PostgreSqlSchemaException>(() =>
            PostgreSqlStateBackend.EnsureSchemaCompatible(
                hasStateTable: false,
                hasComponentsTable: true,
                componentVersion: PostgreSqlSchemaVersions.CoreVersion,
                qualifiedTable: "\"public\".\"configlue_state\"",
                qualifiedComponentsTable: "\"public\".\"configlue_schema_components\""
            )
        );

        exception.Message.ShouldContain("\"public\".\"configlue_state\"");
        exception.Message.ShouldContain("\"public\".\"configlue_schema_components\"");
        exception.Message.ShouldContain("were not found");
    }

    [Test]
    public void EnsureSchemaCompatible_ThrowsWhenTheComponentsTableIsMissing()
    {
        var exception = Should.Throw<PostgreSqlSchemaException>(() =>
            PostgreSqlStateBackend.EnsureSchemaCompatible(
                hasStateTable: true,
                hasComponentsTable: false,
                componentVersion: PostgreSqlSchemaVersions.CoreVersion,
                qualifiedTable: "state",
                qualifiedComponentsTable: "components"
            )
        );

        exception.Message.ShouldContain("were not found");
    }

    [Test]
    public void EnsureSchemaCompatible_ThrowsWhenTheCoreComponentIsNotRegistered()
    {
        var exception = Should.Throw<PostgreSqlSchemaException>(() =>
            PostgreSqlStateBackend.EnsureSchemaCompatible(
                hasStateTable: true,
                hasComponentsTable: true,
                componentVersion: null,
                qualifiedTable: "state",
                qualifiedComponentsTable: "components"
            )
        );

        exception.Message.ShouldContain(PostgreSqlSchemaVersions.CoreComponent);
        exception.Message.ShouldContain("is not registered");
    }

    [Test]
    public void EnsureSchemaCompatible_ThrowsWhenTheCoreVersionIsStale()
    {
        var staleVersion = PostgreSqlSchemaVersions.CoreVersion - 1;
        var exception = Should.Throw<PostgreSqlSchemaException>(() =>
            PostgreSqlStateBackend.EnsureSchemaCompatible(
                hasStateTable: true,
                hasComponentsTable: true,
                componentVersion: staleVersion,
                qualifiedTable: "state",
                qualifiedComponentsTable: "components"
            )
        );

        exception.Message.ShouldContain($"has version {staleVersion}");
        exception.Message.ShouldContain("or later is required");
    }

    [Test]
    public void EnsureSchemaCompatible_AcceptsTheCurrentOrNewerVersion()
    {
        Should.NotThrow(() =>
            PostgreSqlStateBackend.EnsureSchemaCompatible(
                hasStateTable: true,
                hasComponentsTable: true,
                componentVersion: PostgreSqlSchemaVersions.CoreVersion,
                qualifiedTable: "state",
                qualifiedComponentsTable: "components"
            )
        );
        Should.NotThrow(() =>
            PostgreSqlStateBackend.EnsureSchemaCompatible(
                hasStateTable: true,
                hasComponentsTable: true,
                componentVersion: PostgreSqlSchemaVersions.CoreVersion + 1,
                qualifiedTable: "state",
                qualifiedComponentsTable: "components"
            )
        );
    }

    [Test]
    public void SchemaMigrator_RejectsUnsafeTableIdentifiers()
    {
        using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=postgres;Username=postgres;Password=not-used"
        );

        Should.Throw<ArgumentException>(() =>
            new PostgreSqlSchemaMigrator(
                dataSource,
                new PostgreSqlMigrationOptions
                {
                    TableOptions = new PostgreSqlTableOptions
                    {
                        TableName = "state; DROP TABLE users",
                    },
                }
            )
        );
    }
}
