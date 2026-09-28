---
title: HTTP リソースプロトコル
description: Configlue バイトリソースを HTTP 配信し、状態ソースとして使う。
---

# HTTP リソースプロトコル

`Configlue.Resource.Http` は HTTP リーダーと opt-in のライターを提供します。任意パッケージ `Configlue.Resource.Http.AspNetCore` は同じバイトプロトコルを ASP.NET Core Minimal API にマップします。どちらもシリアル化はアプリの状態コーデックに任せます。

## ASP.NET Core エンドポイント

ASP.NET Core アプリに `Configlue.Resource.Http.AspNetCore` を入れ、`IResourceReader` を渡します。HTTP 経由で変更してよいリソースにだけ `IResourceWriter` を渡します:

```csharp
using Configlue.Resources;
using Configlue.State;
using Configlue.Resource.Http.AspNetCore;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication("Configlue").AddCookie("Configlue");
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

IResourceReader reader = GetResourceReader();
IResourceWriter writer = GetResourceWriter();

app.MapConfiglueHttpResource("/api/settings", reader, writer);

await app.RunAsync();
```

既定ルートは `GET /api/settings/get` と `PUT /api/settings/update` です。ライターを渡さなければ更新ルートはマップされません。エンドポイントは既定で認可を要求します。ホスト側で認証・認可を設定してください。意図的に公開する場合に限り `RequireAuthorization = false` を指定します。PUT 本文の既定上限は30,000,000バイトです。`MaximumRequestBodySize` で変更でき、`null` なら無制限です。

`HttpResourceEndpointOptions` でルート相対のパスとペイロードメディア種別を設定します:

```csharp
app.MapConfiglueHttpResource(
    "/api/settings",
    reader,
    writer,
    new HttpResourceEndpointOptions
    {
        GetPath = "state/read",
        UpdatePath = "state/write",
        ContentType = "application/octet-stream",
        MaximumRequestBodySize = 10_000_000,
    });
```

パスは相対で、設定ルート配下に収める必要があります。書き込みは設定メディア種別を使います。既定は `application/octet-stream` です。

## HTTP クライアント

エンドポイントルート (末尾のパス接頭辞込み) でリーダーを作ります。パスとメディア種別はサーバー側と一致させます:

```csharp
using Configlue.Resource.Http;

var reader = new HttpResourceReader(
    httpClient,
    new Uri("https://config.example/api/settings/"));
HttpResourceWriter writer = reader.CreateWriter();
```

ライターを作ってもサーバー側権限は付きません。書き込み意図があるときだけ更新ルートをマップします。読み取り専用ソースはリーダーとコーデックを `SerializedStateSource.FromResource` で合成します。書き込み可能ソースはリモートが更新対応のときだけ `writer: reader.CreateWriter()` を渡します。

クライアントは変更をポーリングし (既定5秒間隔)、条件つき書き込み失敗を `StateConflictException` として出します。ポーリング失敗時は既定で最大30秒まで指数バックオフします。HTTP リクエストのタイムアウトは既定30秒です。必要に応じて `HttpResourceOptions` の `PollingInterval`、`MaximumPollingInterval`、`RequestTimeout` を設定できます。リソース不在は `NotFound` フォールスルーに、輸送失敗・タイムアウトは `Unavailable` にマップされます。スキーマメタデータは `Configlue-Schema-Id`・`Configlue-Schema-Version` ヘッダーで運びます。

登録オプション (`Client`/`ClientFactory`、`Writable`、`WatchChanges`、`FallbackCondition`) は [HTTP と ZIP](../sources/http-and-zip.md) 参照。
