---
title: Redis resource
description: Redis に subject ごとの Configlue state を保存し、Lua による revision check と共有 Pub/Sub invalidation を使う。
---

`Configlue.Resource.Redis` は Redis 保存向けの任意 adapter です。Redis を Configlue の永続 state に使う場合だけインストールします:

```sh
dotnet add package Configlue.Resource.Redis
```

この package は StackExchange.Redis を直接参照します。`Configlue.Core` と `Configlue` メタパッケージは Redis client に依存しません。シリアライズは Configlue codec が担当します。

`ConnectionMultiplexer` を作成して共有し、source を登録します:

```csharp
using Configlue.Provider.Json;
using Configlue.Resource.Redis;
using StackExchange.Redis;

var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConfiguration);

model.Sources(sources => sources
    .FromRedis(new RedisStateSourceOptions
    {
        ResourceNamespace = "app-settings",
        ConnectionMultiplexer = multiplexer,
        Codec = new JsonStateCodec<AppSettings.Fragment>(),
    })
    .Named("redis-settings")
    .Writable());
```

アプリケーションのライフタイム中は multiplexer を保持し、host と一緒に dispose してください。所有権は呼び出し側に残ります。Configlue resource を dispose すると Pub/Sub subscription を解放しますが、multiplexer は dispose しません。StackExchange.Redis は operation ごとの接続ではなく、thread-safe な共有 multiplexer を前提に設計されています。

## Key と物理 route

resource は設定した prefix、resource namespace、`SubjectKey` から Redis hash key を生成します。既定 prefix は `configlue` です。各 hash には payload、integer revision、任意の schema metadata、更新 timestamp を保存します。この provider は key に expiry を設定しません。

`ConnectionMultiplexerResolver` で route ごとに異なる Redis endpoint を選べます。`KeyPrefixSelector` と `DatabaseSelector` から route ごとの prefix や logical database も選択できます:

```csharp
var routeMultiplexers = new Dictionary<RouteKey, IConnectionMultiplexer>
{
    [RouteKey.From("region-a")] = regionAMultiplexer,
    [RouteKey.From("region-b")] = regionBMultiplexer,
};

model.Sources(sources => sources
    .FromRedis(new RedisStateSourceOptions
    {
        ResourceNamespace = "app-settings",
        ConnectionMultiplexerResolver = (_, route) => routeMultiplexers[route],
        ResourceOptions = new RedisResourceOptions
        {
            KeyPrefixSelector = context => $"settings:{context.Route.Value}",
        },
        Codec = new JsonStateCodec<AppSettings.Fragment>(),
    })
    .Named("regional-redis-settings")
    .Writable());
```

物理 route ごとに安定した共有 multiplexer を返してください。ひとつの Configlue resource instance が複数の subject key を扱います。runtime は route 解決を cache し、同じ multiplexer の backend を共有します。Redis Cluster を使う場合、logical database は `0` を指定してください。Redis Cluster は他の logical database を選択できません。

この adapter は Redis の persistence、replication、backup、eviction を設定しません。アプリケーションの durability 要件に合う Redis の運用設定を選んでください。eviction policy により provider key が削除された場合、次回読み取りは `NotFound` になります。provider は TTL を設定せず、state を破棄可能な cache として扱いません。

## Atomic write と変更監視

各書き込みは row key に対するひとつの Lua script で処理します。条件なしの書き込みは revision を増やして byte を置き換えます。`MustNotExist` は同じ script 内で key の不在を確認します。revision match も保存済み revision を比較して同時に更新します。条件が失敗すると `StateConflictException` を送出します。先に読み取って別コマンドで書き込む race はありません。

revision は `1` から始まり decimal string で返ります。script 成功後に prefix・namespace・subject key・database の短い hash を Pub/Sub で送ります。payload と revision は Pub/Sub に含めません。multiplexer と notification channel ごとに共有 subscription を使い、一致する row の waiter だけを起こします。waiter は hash を再読込して正しい byte と revision を取得します。StackExchange.Redis は再接続時に subscription を復元します。さらに provider は接続復旧時に待機中の waiter を起こし、停止中に逃した Pub/Sub 通知を再読込で補います。

Redis ACL には StackExchange.Redis の接続確認用 `ECHO`、Lua 実行用 `EVAL`・`EVALSHA`、hash operation、Pub/Sub の `SUBSCRIBE`・`UNSUBSCRIBE`・`PUBLISH` へのアクセスが必要です。ACL を制限する場合は必要な command と key/channel pattern だけ許可してください。
