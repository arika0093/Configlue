using System.Text.Json;
using System.Text.Json.Serialization;
using Configlue.CompilerServices;
using Configlue.Provider.Json;
using Configlue.Source.PostgreSql;
using Configlue.Source.PostgreSql.Migrations;
using Npgsql;

namespace Configlue.Tests;

/// <summary>
/// Exercises the production PostgreSQL path (migrator, real Npgsql backend, schema guard,
/// transactional CAS, and LISTEN/NOTIFY) against a live PostgreSQL server.
/// </summary>
[PostgreSqlIntegration]
public sealed class PostgreSqlSourceIntegrationTests
{
    [Test]
    public async Task MigrationFromEmptyDatabaseIsIdempotentAndUnblocksRuntime()
    {
        var table = NewTableOptions();
        await using var dataSource = CreateDataSource();
        try
        {
            using var source = CreateSource(dataSource, table);
            var context = CreateContext("tenant");

            await Should.ThrowAsync<PostgreSqlSchemaException>(async () =>
                await source.ReadAsync(context)
            );

            var migrator = new PostgreSqlSchemaMigrator(
                dataSource,
                new PostgreSqlMigrationOptions { TableOptions = table }
            );
            (await migrator.InspectAsync()).IsInitialized.ShouldBeFalse();

            var first = await migrator.MigrateAsync();
            first.Changed.ShouldBeTrue();
            first
                .Components.ShouldContain(static component =>
                    component.Component == PostgreSqlSchemaVersions.CoreComponent
                    && component.Version >= PostgreSqlSchemaVersions.CoreVersion
                );

            var second = await migrator.MigrateAsync();
            second.Changed.ShouldBeFalse();
            (await migrator.InspectAsync()).IsInitialized.ShouldBeTrue();

            (await source.ReadAsync(context)).Status.ShouldBe(StateReadStatus.NotFound);
            var write = await source.WriteAsync(
                context,
                new StateWriteRequest<string>("first", RevisionCondition.MustNotExist)
            );
            write.Revision.ShouldBe("1");
        }
        finally
        {
            await DropTablesAsync(dataSource, table);
        }
    }

    [Test]
    public async Task ConditionalWritesAllowExactlyOneConcurrentWriter()
    {
        var table = NewTableOptions();
        await using var dataSource = CreateDataSource();
        try
        {
            await MigrateAsync(dataSource, table);
            using var source = CreateSource(dataSource, table);
            var context = CreateContext("tenant");

            var creates = await Task.WhenAll(
                Enumerable
                    .Range(0, 24)
                    .Select(index =>
                        TryWriteAsync(
                            source,
                            context,
                            index.ToString(),
                            RevisionCondition.MustNotExist
                        )
                    )
            );
            creates.Count(static succeeded => succeeded).ShouldBe(1);
            (await source.ReadAsync(context)).Revision.ShouldBe("1");

            await Should.ThrowAsync<StateConflictException>(async () =>
                await source.WriteAsync(
                    context,
                    new StateWriteRequest<string>("again", RevisionCondition.MustNotExist)
                )
            );

            var matches = await Task.WhenAll(
                Enumerable
                    .Range(0, 24)
                    .Select(index =>
                        TryWriteAsync(source, context, index.ToString(), RevisionCondition.Match("1"))
                    )
            );
            matches.Count(static succeeded => succeeded).ShouldBe(1);
            (await source.ReadAsync(context)).Revision.ShouldBe("2");

            var unconditional = await source.WriteAsync(
                context,
                new StateWriteRequest<string>("unconditional")
            );
            unconditional.Revision.ShouldBe("3");
        }
        finally
        {
            await DropTablesAsync(dataSource, table);
        }
    }

    [Test]
    public async Task RowsAreIsolatedByModelSubjectNamespaceAndRoute()
    {
        var table = NewTableOptions();
        await using var primary = CreateDataSource();
        var secondaryDatabase = "configlue_it_" + Guid.NewGuid().ToString("N")[..20];
        await using var admin = CreateDataSource("postgres");
        await CreateDatabaseAsync(admin, secondaryDatabase);
        var secondary = CreateDataSource(secondaryDatabase);
        try
        {
            await MigrateAsync(primary, table);
            await MigrateAsync(secondary, table);

            var routeA = RouteKey.From("region-a");
            var routeB = RouteKey.From("region-b");
            using var source = new PostgreSqlSource<string>(
                route => route == routeA ? primary : secondary,
                "settings",
                new JsonStateValueSerializer<string>(),
                table
            );

            var modelOne = CreateContext("tenant", routeA, "model-one");
            var modelTwo = CreateContext("tenant", routeA, "model-two");
            var otherSubject = CreateContext("other", routeA);
            var otherRoute = CreateContext("tenant", routeB);

            await source.WriteAsync(
                modelOne,
                new StateWriteRequest<string>("one", RevisionCondition.MustNotExist)
            );
            await source.WriteAsync(
                modelTwo,
                new StateWriteRequest<string>("two", RevisionCondition.MustNotExist)
            );
            await source.WriteAsync(
                otherSubject,
                new StateWriteRequest<string>("three", RevisionCondition.MustNotExist)
            );
            await source.WriteAsync(
                otherRoute,
                new StateWriteRequest<string>("four", RevisionCondition.MustNotExist)
            );

            (await source.ReadAsync(modelOne)).Value.ShouldBe("one");
            (await source.ReadAsync(modelTwo)).Value.ShouldBe("two");
            (await source.ReadAsync(otherSubject)).Value.ShouldBe("three");
            (await source.ReadAsync(otherRoute)).Value.ShouldBe("four");

            source.GetResourceId(modelOne).ShouldNotBe(source.GetResourceId(modelTwo));
            source.GetResourceId(modelOne).ShouldNotBe(source.GetResourceId(otherSubject));
            source.GetResourceId(modelOne).ShouldNotBe(source.GetResourceId(otherRoute));

            (await primary.ReadRowAsync(table, "model-one", "settings", "tenant")).ShouldBe("one");
            (await secondary.ReadRowAsync(table, string.Empty, "settings", "tenant"))
                .ShouldBe("four");
        }
        finally
        {
            await DropTablesAsync(primary, table);
            await DropTablesAsync(secondary, table);
            await secondary.DisposeAsync();
            await DropDatabaseAsync(admin, secondaryDatabase);
        }
    }

    [Test]
    public async Task PayloadSchemaModelMismatchIsRejectedAndMatchingSchemaPersists()
    {
        var table = NewTableOptions();
        await using var dataSource = CreateDataSource();
        try
        {
            await MigrateAsync(dataSource, table);
            using var source = new PostgreSqlSource<TestFragment>(
                dataSource,
                "settings",
                JsonStateValueSerializer<TestFragment>.FromConverter(new TestFragmentConverter()),
                table
            );
            var context = CreateContext("tenant", modelId: "model-one");

            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await source.WriteAsync(
                    context,
                    new StateWriteRequest<TestFragment>(new TestFragment("model-two", 1))
                )
            );

            var write = await source.WriteAsync(
                context,
                new StateWriteRequest<TestFragment>(
                    new TestFragment("model-one", 1),
                    RevisionCondition.MustNotExist
                )
            );
            write.Revision.ShouldBe("1");

            var read = await source.ReadAsync(context);
            read.Status.ShouldBe(StateReadStatus.Success);
            read.Schema.HasValue.ShouldBeTrue();
            read.Schema!.Value.ModelId.ShouldBe("model-one");
        }
        finally
        {
            await DropTablesAsync(dataSource, table);
        }
    }

    [Test]
    public async Task ListenNotifyDeliversAndScopesNotificationsByIdentity()
    {
        var table = NewTableOptions();
        await using var writerDataSource = CreateDataSource();
        await using var watcherDataSource = CreateDataSource();
        try
        {
            await MigrateAsync(writerDataSource, table);
            using var writer = CreateSource(writerDataSource, table);
            using var watcher = CreateSource(watcherDataSource, table);
            var first = CreateContext("first");
            var second = CreateContext("second");
            await writer.WriteAsync(
                first,
                new StateWriteRequest<string>("1", RevisionCondition.MustNotExist)
            );

            var firstWait = watcher.WaitForChangeAsync(first, "1").AsTask();
            var secondWait = watcher.WaitForChangeAsync(second, null).AsTask();

            await Task.Delay(400);
            firstWait.IsCompleted.ShouldBeFalse();
            secondWait.IsCompleted.ShouldBeFalse();

            await writer.WriteAsync(
                first,
                new StateWriteRequest<string>("2", RevisionCondition.Match("1"))
            );
            await firstWait.WaitAsync(TimeSpan.FromSeconds(15));
            secondWait.IsCompleted.ShouldBeFalse();

            await writer.WriteAsync(second, new StateWriteRequest<string>("3"));
            await secondWait.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await DropTablesAsync(writerDataSource, table);
        }
    }

    [Test]
    public async Task WatcherRecoversWhenItsListenConnectionIsTerminated()
    {
        var table = NewTableOptions();
        var applicationName = "configlue-itest-" + Guid.NewGuid().ToString("N")[..16];
        await using var writerDataSource = CreateDataSource();
        await using var watcherDataSource = CreateDataSource(applicationName: applicationName);
        await using var adminDataSource = CreateDataSource();
        try
        {
            await MigrateAsync(writerDataSource, table);
            using var writer = CreateSource(writerDataSource, table);
            using var watcher = CreateSource(watcherDataSource, table);
            var context = CreateContext("tenant");
            await writer.WriteAsync(
                context,
                new StateWriteRequest<string>("1", RevisionCondition.MustNotExist)
            );

            var wait = watcher.WaitForChangeAsync(context, "1").AsTask();
            await Task.Delay(400);

            await adminDataSource.TerminateConnectionsAsync(applicationName);

            await wait.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await DropTablesAsync(writerDataSource, table);
        }
    }

    [Test]
    public async Task CancellationAndDisposalReleaseActiveWatchers()
    {
        var table = NewTableOptions();
        await using var dataSource = CreateDataSource();
        try
        {
            await MigrateAsync(dataSource, table);
            var context = CreateContext("tenant");
            using (var source = CreateSource(dataSource, table))
            {
                await source.WriteAsync(
                    context,
                    new StateWriteRequest<string>("1", RevisionCondition.MustNotExist)
                );

                using var cancellation = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(300)
                );
                await Should.ThrowAsync<OperationCanceledException>(async () =>
                    await source.WaitForChangeAsync(context, "1", cancellation.Token)
                );

                var wait = source.WaitForChangeAsync(context, "1").AsTask();
                await Task.Delay(400);
                source.Dispose();
                await wait.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        finally
        {
            await DropTablesAsync(dataSource, table);
        }
    }

    private static string NewTableName() => "configlue_it_" + Guid.NewGuid().ToString("N")[..20];

    private static PostgreSqlTableOptions NewTableOptions() =>
        new()
        {
            TableName = NewTableName(),
            ComponentsTableName = NewTableName() + "_c",
        };

    private static PostgreSqlSource<string> CreateSource(
        NpgsqlDataSource dataSource,
        PostgreSqlTableOptions table
    ) => new(dataSource, "settings", new JsonStateValueSerializer<string>(), table);

    private static NpgsqlDataSource CreateDataSource(
        string? database = null,
        string? applicationName = null
    )
    {
        var builder = new NpgsqlConnectionStringBuilder(
            IntegrationEnvironment.PostgreSqlConnectionString
        );
        if (database is not null)
        {
            builder.Database = database;
        }

        if (applicationName is not null)
        {
            builder.ApplicationName = applicationName;
        }

        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private static async Task MigrateAsync(
        NpgsqlDataSource dataSource,
        PostgreSqlTableOptions table
    )
    {
        var migrator = new PostgreSqlSchemaMigrator(
            dataSource,
            new PostgreSqlMigrationOptions { TableOptions = table }
        );
        (await migrator.MigrateAsync()).Changed.ShouldBeTrue();
    }

    private static async Task<bool> TryWriteAsync(
        PostgreSqlSource<string> source,
        ConfiglueResourceContext context,
        string value,
        RevisionCondition condition
    )
    {
        try
        {
            await source.WriteAsync(context, new StateWriteRequest<string>(value, condition));
            return true;
        }
        catch (StateConflictException)
        {
            return false;
        }
    }

    private static ConfiglueResourceContext CreateContext(
        string subject,
        RouteKey route = default,
        string? modelId = null
    )
    {
        var key = SubjectKey.From(subject);
        return new ConfiglueResourceContext(
            modelId,
            new IntegrationSubject(key),
            ResourceKey.From(key),
            route
        );
    }

    private static async Task CreateDatabaseAsync(NpgsqlDataSource admin, string database)
    {
        await using var connection = await admin.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(NpgsqlDataSource admin, string database)
    {
        await using var connection = await admin.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropTablesAsync(
        NpgsqlDataSource dataSource,
        PostgreSqlTableOptions table
    )
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP TABLE IF EXISTS {Quote(table.TableName)}; "
            + $"DROP TABLE IF EXISTS {Quote(table.ComponentsTableName)}";
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"") + '"';

    private sealed record TestFragment(string ModelId, int Version) : IConfiglueDynamicFragment
    {
        public ConfiglueModelSchema Schema =>
            new(typeof(TestFragment), ModelId, Version, []);

        public IEnumerable<ConfiglueFragmentMember> EnumeratePresentMembers() => [];

        public IConfiglueFragment WithMember(int memberId, object? value) => this;

        public IConfiglueFragment WithoutMember(int memberId) => this;
    }

    private sealed class TestFragmentConverter : JsonConverter<TestFragment>
    {
        public override TestFragment? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        )
        {
            string? modelId = null;
            var version = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var name = reader.GetString();
                reader.Read();
                if (name == "modelId")
                {
                    modelId = reader.GetString();
                }
                else if (name == "version")
                {
                    version = reader.GetInt32();
                }
            }

            return modelId is null ? null : new TestFragment(modelId, version);
        }

        public override void Write(
            Utf8JsonWriter writer,
            TestFragment value,
            JsonSerializerOptions options
        )
        {
            writer.WriteStartObject();
            writer.WriteString("modelId", value.ModelId);
            writer.WriteNumber("version", value.Version);
            writer.WriteEndObject();
        }
    }

    private sealed record IntegrationSubject(SubjectKey Key) : IConfiglueSubject;
}

internal static class PostgreSqlIntegrationExtensions
{
    public static async Task<string?> ReadRowAsync(
        this NpgsqlDataSource dataSource,
        PostgreSqlTableOptions table,
        string modelId,
        string resourceNamespace,
        string subjectKey
    )
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT \"payload\" #>> '{{}}' FROM {Quote(table.TableName)} "
            + "WHERE \"model_id\" = @model_id AND \"resource_namespace\" = @namespace "
            + "AND \"subject_key\" = @subject_key";
        command.Parameters.AddWithValue("model_id", modelId);
        command.Parameters.AddWithValue("namespace", resourceNamespace);
        command.Parameters.AddWithValue("subject_key", SubjectKey.From(subjectKey).Value);
        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    public static async Task TerminateConnectionsAsync(
        this NpgsqlDataSource dataSource,
        string applicationName
    )
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity "
            + "WHERE application_name = @application_name AND pid <> pg_backend_pid()";
        command.Parameters.AddWithValue("application_name", applicationName);
        await command.ExecuteNonQueryAsync();
    }

    private static string Quote(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"") + '"';
}
