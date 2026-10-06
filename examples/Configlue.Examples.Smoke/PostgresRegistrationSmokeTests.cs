using Configlue;
using Configlue.Examples.Shared;
using Configlue.Source.PostgreSql;
using Configlue.Source.PostgreSql.Migrations;
using Npgsql;
using Shouldly;
using TUnit.Core;

namespace Configlue.Examples.Smoke;

// Lightweight smoke coverage for the Playground PostgreSQL-backed scenario.
// Only the registration path is exercised: building the context must not open
// connections, so this runs with no database server. Live behavior stays in
// the PostgreSQL integration tests and the runnable Playground.
public sealed class PostgresRegistrationSmokeTests
{
    [Test]
    public async Task PostgresSource_RegistrationBuildsWithoutConnecting()
    {
        // Building a data source never connects; there is no server here.
        await using var dataSource = new NpgsqlDataSourceBuilder(
            "Host=localhost;Username=postgres;Password=postgres;Database=configlue"
        ).Build();
        var tableOptions = new PostgreSqlTableOptions();

        // The migrator owns DDL and constructs without touching the database.
        _ = new PostgreSqlSchemaMigrator(
            dataSource,
            new PostgreSqlMigrationOptions { TableOptions = tableOptions }
        );

        await using var context = ConfiglueApp.CreateContext(config =>
        {
            config.Add<SharedBoardSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromPostgreSql(
                        new PostgreSqlSourceOptions
                        {
                            Id = "postgres",
                            ResourceNamespace = "smoke",
                            DataSource = dataSource,
                            TableOptions = tableOptions,
                        }
                    )
                );
                model.Writes(write =>
                    write.DefaultTo(SourceKey<SharedBoardSettings>.Named("postgres"))
                );
            });
        });

        context.GetState<SharedBoardSettings>().ShouldNotBeNull();
    }
}
