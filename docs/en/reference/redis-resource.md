---
title: Redis resource
description: Store subject-scoped Configlue state in Redis with atomic Lua revision checks and shared Pub/Sub invalidation.
---

`Configlue.Resource.Redis` is an optional Redis storage adapter. Install it only when the application uses Redis for persistent Configlue state:

```sh
dotnet add package Configlue.Resource.Redis
```

The package references StackExchange.Redis directly. `Configlue.Core` and the `Configlue` meta-package do not depend on a Redis client. Serialization stays in the Configlue codec.

Create and share one `ConnectionMultiplexer`, then register a source:

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

Keep the multiplexer alive for the application lifetime and dispose it with the host. It remains caller-owned; disposing the Configlue resource releases its Pub/Sub subscription without disposing the multiplexer. StackExchange.Redis is designed for a shared, thread-safe multiplexer rather than one connection per operation.

## Keys and physical routes

The resource generates one Redis hash key from the configured prefix, resource namespace, and `SubjectKey`. The default prefix is `configlue`. Each hash stores the payload, an integer revision, optional schema metadata, and an update timestamp. Keys have no expiry set by this provider.

Routes can select separate Redis endpoints with `ConnectionMultiplexerResolver`; `KeyPrefixSelector` and `DatabaseSelector` can also choose a route-specific prefix or logical database:

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

Return a stable, shared multiplexer for each physical route. One Configlue resource instance serves all of its subject keys. The resource caches route resolution and shares its backend for the same multiplexer. When using Redis Cluster, use database `0`; Redis Cluster does not support selecting other logical databases.

This adapter does not configure Redis persistence, replication, backups, or eviction. Choose Redis deployment settings that fit the application's durability requirements. An eviction policy that removes these provider-generated keys makes a later read return `NotFound`; the provider does not apply TTLs or treat state as disposable cache data.

## Atomic writes and watching

Every write runs one Lua script against the row key. An unconditional write increments the revision and replaces the bytes. `MustNotExist` checks key absence in the same script. A matching revision compares the stored revision and updates it atomically. Failed conditions raise `StateConflictException`; the provider never reads first and then races a separate write.

Revisions start at `1` and are returned as decimal strings. Successful scripts publish a short hash of the prefix, namespace, subject key, and database. Payloads and revisions are not sent over Pub/Sub. A shared subscription is used for each multiplexer and notification channel; it wakes only waiters for the matching row, and those waiters read the hash again for authoritative bytes and revision. StackExchange.Redis restores subscriptions after reconnect. The provider also wakes all active waiters after a connection is restored so they re-read state and recover from Pub/Sub messages missed during the outage.

The Redis ACL used by this adapter needs access to `ECHO` for StackExchange.Redis connection checks, Lua evaluation (`EVAL` and `EVALSHA`), hash operations, and Pub/Sub `SUBSCRIBE`, `UNSUBSCRIBE`, and `PUBLISH`. Applications with a restricted ACL should grant only the required commands and key/channel patterns.
