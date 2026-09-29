---
title: PostgreSQL resource
description: subject ごとの Configlue byte を PostgreSQL に保存し、atomic revision check と change notification を使う。
---

`Configlue.Resource.PostgreSql` は PostgreSQL へ直接保存する任意の adapter です。PostgreSQL が必要なアプリケーションだけにインストールします:

```sh
dotnet add package Configlue.Resource.PostgreSql
```

この package は Npgsql を直接参照します。`Configlue.Core` と `Configlue` メタパッケージは Npgsql に依存しません。これは byte resource adapter であり、ORM や query layer ではありません。シリアライズは Configlue codec が担当します。

共有する `NpgsqlDataSource` を作成して保持し、source を登録します:

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

アプリケーションのライフタイム中は data source を保持し、host と一緒に dispose してください。所有権は呼び出し側に残り、Configlue は resource runtime と通知 listener だけを dispose します。Npgsql は connection pool の設定を保持する `NpgsqlDataSource` の共有を推奨しています。

既定では初回利用時に `public` schema と `configlue_state` table を作成します。各 row は `(resource_namespace, subject_key)` で指定し、byte、単調増加する `bigint` revision、任意の schema metadata、`updated_at` timestamp を保存します。resource namespace は同じ table を共有するアプリケーションや設定群を分離します。既定の subject key は空文字列なので、サーバー共通の state も同じ形式で保存されます。

database role には設定 schema と table を作成する権限が必要です。`PostgreSqlResourceOptions.SchemaName` と `TableName` で場所を変更できます。identifier は ASCII 英字・数字・underscore のみに制限し、生成 SQL 内で quote します。

## Route と subject key

resource context は operation ごとの `SubjectKey` を渡します。route resolver は物理配置 route を、共有する呼び出し側所有の data source に対応付けます:

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

物理 database route ごとに同じ data source instance を使ってください。runtime は route 解決を cache し、同じ data source 上の全 subject key で backend と notification listener を共有します。resource identity には subject key が含まれ、route resolver を設定した場合は選択 route も含まれます。

## Revision と変更監視

条件なしの書き込みは atomic な PostgreSQL upsert を使います。`MustNotExist` は `INSERT ... ON CONFLICT DO NOTHING`、revision match は `UPDATE ... WHERE revision = @expected` を実行します。条件が一致しない場合は `StateConflictException` を送出します。読み取りと書き込みを別々に実行して race を作ることはありません。

revision は `1` から始まり、不変形式の decimal string として返します。row 更新と `pg_notify` は同じ transaction で commit します。data source backend の最初の watcher が始まると共有 `LISTEN` connection を開き、同じ backend 上のすべての subject waiter が再利用します。通知には namespace と subject key の短い hash だけを含めます。一致する通知を受けた waiter は row を再読込し、正しい byte と revision を取得します。listener は切断後に再接続・再購読し、停止中に通知を逃した可能性があるため、接続復旧時には待機中の全 waiter を起こして再読込させます。

data source 上で watch する table ごとに、その table を使う resource が動作している間は専用 connection を1つ保持します。PostgreSQL の connection 上限を考慮してください。Configlue source context を dispose すると listener lease は停止しますが、アプリケーション所有の data source は dispose しません。

この provider は Npgsql ADO.NET command と PostgreSQL 組み込み型のみを使います。dynamic JSON mapping や ORM の model reflection は有効化しません。Npgsql は version 8.0 以降の NativeAOT と trimming 対応を[互換性ノート](https://www.npgsql.org/doc/compatibility.html)で説明しています。他の Npgsql mapping を使うアプリケーションは、その機能固有の要件に従ってください。
