---
title: PostgreSQL source
description: 型付き Configlue state を PostgreSQL JSONB に直接保存し、atomic revision check と change notification を使う。
---

`Configlue.Source.PostgreSql` は JSONB native の任意 PostgreSQL source です。database schema の作成は分離された `Configlue.Source.PostgreSql.Migrations` package が所有します。

```sh
dotnet add package Configlue.Source.PostgreSql
dotnet add package Configlue.Source.PostgreSql.Migrations
```

どちらの package も Npgsql を直接参照します。`Configlue.Core` と `Configlue` メタパッケージは Npgsql に依存しません。runtime package は Configlue の JSON シリアライズ意味論を重複させず再利用するため `Configlue.Provider.Json` に依存します。migrations package は runtime package に依存し、逆方向の依存はありません。

## database schema の適用

runtime source は database object を作成・変更しません。まず `PostgreSqlSchemaMigrator` で schema を適用します。DDL 権限を持つ principal を使うことを推奨します:

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

`MigrateAsync` は schema、core state table、その primary key と lookup index、component version table を作成します。`InspectAsync` は何も変更せずに観測済みの component version を返します。component version は component ごとに管理するため、将来の任意機能が独自の version track を持てます。

state table や component version table が存在しない、または `core` component が runtime の要求より古い database で runtime source を開始すると、database を黙って変更せず、対処方法を記した `PostgreSqlSchemaException` を送出します。

## source の登録

runtime DML 用の共有 `NpgsqlDataSource` を作成して保持し、source を登録します:

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

DDL と DML で別 principal を使うアプリケーションは、migration principal で migrator を実行し、DML のみの principal で source を実行できます。

登録済みの生成 fragment converter は自動的に使われます。trimming と NativeAOT には source-generated resolver を設定した `SerializerOptions` を渡してください。

## JSONB storage

row は `(model_id, resource_namespace, subject_key)` で指定します。schema metadata は JSON payload 内ではなく relational column に保持します:

```text
model_id       = app-settings
schema_version = 3
payload        = {"RetryCount":10,...}
```

payload column は `jsonb NOT NULL` です。`schema_version` column は `integer NOT NULL`、`revision` は単調増加する `bigint` です。runtime は JSON 値に `$version` や `$configlue` metadata を埋め込みません。model ID は安定した Configlue model identity なので、異なる model が同じ resource namespace と table を共有しても衝突しません。既定の subject key は空文字列なので、サーバー共通の state も同じ形式で保存されます。

identifier は ASCII 英字・数字・underscore のみに制限し、生成 SQL 内で quote します。

## Route と subject key

resource context は operation ごとの `SubjectKey` を渡します。route resolver は物理配置 route を、共有する呼び出し側所有の data source に対応付けます:

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

物理 database route ごとに同じ data source instance を使ってください。runtime は route 解決を cache し、同じ data source 上の全 subject key で backend と notification listener を共有します。source identity には model ID と subject key が含まれ、route resolver を設定した場合は選択 route も含まれます。

## Revision と変更監視

条件なしの書き込みは atomic な PostgreSQL upsert を使います。`MustNotExist` は `INSERT ... ON CONFLICT DO NOTHING`、revision match は `UPDATE ... WHERE revision = @expected` を実行します。条件が一致しない場合は `StateConflictException` を送出します。読み取りと書き込みを別々に実行して race を作ることはありません。

revision は `1` から始まり、不変形式の decimal string として返します。row 更新と `pg_notify` は同じ transaction で commit します。data source backend の最初の watcher が始まると共有 `LISTEN` connection を開き、同じ backend 上のすべての subject waiter が再利用します。通知には namespace、model ID、subject key の短い hash だけを含めます。一致する通知を受けた waiter は row を再読込し、正しい値と revision を取得します。listener は切断後に再接続・再購読し、停止中に通知を逃した可能性があるため、接続復旧時には待機中の全 waiter を起こして再読込させます。

data source 上で watch する table ごとに、その table を使う source が動作している間は専用 connection を1つ保持します。PostgreSQL の connection 上限を考慮してください。Configlue source context を dispose すると listener lease は停止しますが、アプリケーション所有の data source は dispose しません。

この provider は Npgsql ADO.NET command と PostgreSQL 組み込み型のみを使います。dynamic JSON mapping や ORM の model reflection は有効化しません。Npgsql は version 8.0 以降の NativeAOT と trimming 対応を[互換性ノート](https://www.npgsql.org/doc/compatibility.html)で説明しています。他の Npgsql mapping を使うアプリケーションは、その機能固有の要件に従ってください。
