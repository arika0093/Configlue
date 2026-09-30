---
title: PostgreSQL resource
description: Store subject-scoped Configlue bytes directly in PostgreSQL with atomic revision checks and change notifications.
---

`Configlue.Resource.PostgreSql` is an optional direct PostgreSQL storage adapter. Install it only when the application needs PostgreSQL:

```sh
dotnet add package Configlue.Resource.PostgreSql
```

The package references Npgsql directly; `Configlue.Core` and the `Configlue` meta-package do not depend on Npgsql. This is a byte-resource adapter, not an ORM or query layer. Serialization remains in the Configlue codec.

Create and retain a shared `NpgsqlDataSource`, then register a source:

```csharp
using Configlue.Provider.Json;
using Configlue.Resource.PostgreSql;
using Npgsql;

var dataSource = NpgsqlDataSource.Create(connectionString);

model.Sources(sources => sources
    .FromPostgreSql(new PostgreSqlStateSourceOptions
    {
        ResourceNamespace = "app-settings",
        DataSource = dataSource,
        Codec = new JsonStateCodec<AppSettings.Fragment>(),
    })
    .Named("postgres-settings")
    .Writable());
```

Keep the data source alive for the application lifetime and dispose it with the host. It remains caller-owned; Configlue disposes only the resource runtime and its notification listener. Npgsql recommends sharing `NpgsqlDataSource` instances because each data source owns connection-pool configuration and pooling.

By default, the provider creates schema `public` and table `configlue_state` on first use. Each row is addressed by `(model_id, resource_namespace, subject_key)` and stores the bytes, a monotonically increasing `bigint` revision, an optional `schema_version`, and an `updated_at` timestamp. The model ID is the stable Configlue model identity, so different models can share one resource namespace and table without colliding. The source's resource namespace separates applications or settings collections that share a table. The default subject key is the empty string, so server-wide state uses the same storage model.

The database role needs permission to create the configured schema and table. Set `PostgreSqlResourceOptions.SchemaName` and `TableName` to choose another location. Identifiers are restricted to ASCII letters, digits, and underscores and are quoted in generated SQL.

## Routes and subject keys

The resource context supplies a `SubjectKey` for each operation. The route resolver maps physical placement routes to shared, caller-owned data sources:

```csharp
var regionalDataSources = new Dictionary<RouteKey, NpgsqlDataSource>
{
    [RouteKey.From("region-a")] = regionADataSource,
    [RouteKey.From("region-b")] = regionBDataSource,
};

model.Sources(sources => sources
    .FromPostgreSql(new PostgreSqlStateSourceOptions
    {
        ResourceNamespace = "app-settings",
        DataSourceResolver = (_, route) => regionalDataSources[route],
        Codec = new JsonStateCodec<AppSettings.Fragment>(),
    })
    .Named("regional-postgres-settings")
    .Writable());
```

Use one stable data source instance per physical database route. The runtime caches the route resolution and shares its backend and notification listener among all subject keys on that data source. Resource identity includes the model ID, the subject key, and, when you configure a route resolver, the selected route.

## Revisions and watching

Writes with no condition use an atomic PostgreSQL upsert. `MustNotExist` uses `INSERT ... ON CONFLICT DO NOTHING`; a matching revision uses one `UPDATE ... WHERE revision = @expected` statement. A failed condition raises `StateConflictException`; the provider does not read first and then race a write.

Revisions start at `1` and are returned as invariant decimal strings. The provider commits the row update and `pg_notify` call together. A shared `LISTEN` connection is opened for a data-source backend when its first watcher starts and is reused by waiters for every subject key on that backend. Notifications contain only a short hash of the namespace, model ID, and subject key. A matching notification wakes the waiter, which reads the row again to get authoritative bytes and revision. After a listener reconnects and re-subscribes, active waiters are invalidated so they also re-read and recover from notifications missed during the outage.

For each watched table on a data source, the runtime keeps one dedicated connection checked out while a resource using that table is alive. Plan PostgreSQL connection limits accordingly. Disposing the Configlue source context stops its listener lease; it does not dispose the application data source.

The provider uses Npgsql's ADO.NET commands and built-in PostgreSQL types only; it does not enable dynamic JSON mapping or use ORM model reflection. Npgsql documents NativeAOT and trimming compatibility starting with version 8.0 in its [compatibility notes](https://www.npgsql.org/doc/compatibility.html). Applications using additional Npgsql mappings should follow Npgsql's requirements for those features.
