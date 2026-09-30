---
title: PostgreSQL source
description: Store typed Configlue state directly in PostgreSQL JSONB with atomic revision checks and change notifications.
---

`Configlue.Source.PostgreSql` is an optional, JSONB-native PostgreSQL source. Database schema creation is owned by the separate `Configlue.Source.PostgreSql.Migrations` package.

```sh
dotnet add package Configlue.Source.PostgreSql
dotnet add package Configlue.Source.PostgreSql.Migrations
```

Both packages reference Npgsql directly; `Configlue.Core` and the `Configlue` meta-package do not depend on Npgsql. The runtime package depends on `Configlue.Provider.Json` so it can reuse Configlue's JSON serialization semantics without duplicating them. The migrations package depends on the runtime package, never the other way around.

## Apply the database schema

The runtime source never creates or alters database objects. Apply the schema first with `PostgreSqlSchemaMigrator`, preferably with a principal that holds DDL privileges:

```csharp
using Configlue.Source.PostgreSql;
using Configlue.Source.PostgreSql.Migrations;
using Npgsql;

await using var migrationDataSource = NpgsqlDataSource.Create(migrationConnectionString);
var migrator = new PostgreSqlSchemaMigrator(
    migrationDataSource,
    new PostgreSqlMigrationOptions
    {
        TableOptions = new PostgreSqlTableOptions { SchemaName = "public", TableName = "configlue_state" },
    }
);

await migrator.MigrateAsync();
```

`MigrateAsync` creates the schema, the core state table, its primary key and lookup index, and the component-version table. A call to `InspectAsync` reports the observed component versions without changing anything. Component versions are tracked per component, so future optional features can maintain their own version tracks without forcing every database to adopt one global version.

If the runtime source starts against a database where the state table or component-version table is missing, or where the `core` component is older than the version the runtime requires, it throws `PostgreSqlSchemaException` with a remediation message instead of silently mutating the database.

## Register the source

Create and retain a shared `NpgsqlDataSource` for runtime DML, then register a source:

```csharp
using Configlue.Source.PostgreSql;
using Npgsql;

var dataSource = NpgsqlDataSource.Create(connectionString);

model.Sources(sources => sources
    .FromPostgreSql(new PostgreSqlSourceOptions
    {
        ResourceNamespace = "app-settings",
        DataSource = dataSource,
    })
    .Named("postgres-settings")
    .Writable());
```

Applications that use different principals for DDL and DML can run the migrator with a migration principal and the source with a DML-only principal.

Generated fragment converters are used automatically when registered. Supply `SerializerOptions` with a source-generated resolver for trimming and NativeAOT.

## JSONB storage

Rows are addressed by `(model_id, resource_namespace, subject_key)`. Schema metadata lives in relational columns rather than inside the JSON payload:

```text
model_id       = app-settings
schema_version = 3
payload        = {"RetryCount":10,...}
```

The payload column is `jsonb NOT NULL`. The `schema_version` column is `integer NOT NULL`, and `revision` is a monotonic `bigint`. The runtime does not embed `$version` or `$configlue` metadata in the JSON value. The model ID is the stable Configlue model identity, so different models can share one resource namespace and table without colliding. The default subject key is the empty string, so server-wide state uses the same storage model.

Identifiers are restricted to ASCII letters, digits, and underscores and are quoted in generated SQL.

## Routes and subject keys

The resource context supplies a `SubjectKey` for each operation. The route resolver maps physical placement routes to shared, caller-owned data sources:

```csharp
var regionalDataSources = new Dictionary<RouteKey, NpgsqlDataSource>
{
    [RouteKey.From("region-a")] = regionADataSource,
    [RouteKey.From("region-b")] = regionBDataSource,
};

model.Sources(sources => sources
    .FromPostgreSql(new PostgreSqlSourceOptions
    {
        ResourceNamespace = "app-settings",
        DataSourceResolver = (_, route) => regionalDataSources[route],
    })
    .Named("regional-postgres-settings")
    .Writable());
```

Use one stable data source instance per physical database route. The runtime caches the route resolution and shares its backend and notification listener among all subject keys on that data source. Source identity includes the model ID, the subject key, and, when you configure a route resolver, the selected route.

## Revisions and watching

Writes with no condition use an atomic PostgreSQL upsert. `MustNotExist` uses `INSERT ... ON CONFLICT DO NOTHING`; a matching revision uses one `UPDATE ... WHERE revision = @expected` statement. A failed condition raises `StateConflictException`; the provider does not read first and then race a write.

Revisions start at `1` and are returned as invariant decimal strings. The provider commits the row update and `pg_notify` call together. A shared `LISTEN` connection is opened for a data-source backend when its first watcher starts and is reused by waiters for every subject key on that backend. Notifications contain only a short hash of the namespace, model ID, and subject key. A matching notification wakes the waiter, which reads the row again to get the authoritative value and revision. After a listener reconnects and re-subscribes, active waiters are invalidated so they also re-read and recover from notifications missed during the outage.

For each watched table on a data source, the runtime keeps one dedicated connection checked out while a source using that table is alive. Plan PostgreSQL connection limits accordingly. Disposing the Configlue source context stops its listener lease; it does not dispose the application data source.

The provider uses Npgsql's ADO.NET commands and built-in PostgreSQL types only; it does not enable dynamic JSON mapping or use ORM model reflection. Npgsql documents NativeAOT and trimming compatibility starting with version 8.0 in its [compatibility notes](https://www.npgsql.org/doc/compatibility.html). Applications using additional Npgsql mappings should follow Npgsql's requirements for those features.
