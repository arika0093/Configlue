---
title: HTTP リソースプロトコル
description: Configlue のバイトリソースを HTTP で公開し、状態ソースとして利用します。
---

# HTTP リソースプロトコル

`Configlue.Resource.Http` は HTTP リーダーと、明示的に作成するライターを提供します。オプションの `Configlue.Resource.Http.AspNetCore` パッケージは、同じバイト単位のプロトコルを ASP.NET Core Minimal API にマッピングします。どちらのパッケージもシリアライズはアプリケーションの状態コーデックに任せます。

## ASP.NET Core エンドポイント

ASP.NET Core アプリケーションに `Configlue.Resource.Http.AspNetCore` を追加し、`IResourceReader` を渡します。HTTP 経由でリソースを変更できる場合に限り、`IResourceWriter` も渡してください。

```csharp
using Configlue;
using Configlue.Resource.Http.AspNetCore;

var app = WebApplication.CreateBuilder(args).Build();

IResourceReader reader = GetResourceReader();
IResourceWriter writer = GetResourceWriter();

app.MapConfiglueHttpResource("/api/settings", reader, writer)
    .RequireAuthorization();

await app.RunAsync();
```

既定のルートは `GET /api/settings/get` と `PUT /api/settings/update` です。ライターを渡さない場合、更新ルートはマッピングされません。戻り値のルートグループには、認可などのエンドポイント規約を設定できます。

`HttpResourceEndpointOptions` でルート内の相対パスとペイロードのメディアタイプを設定できます。

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
    });
```

パスは相対パスで、クエリやフラグメントを含まず、設定したルートの範囲内に収まる必要があります。更新リクエストは設定したメディアタイプを使います。既定値は `application/octet-stream` です。

## HTTP の動作

| リクエストまたはリソースの結果 | 応答または動作 |
| --- | --- |
| `GET` が `Success` を返す | `200`、リソースのバイト列、`ETag`、存在する場合はスキーマヘッダー |
| `GET` が `NotFound` を返す | `404` |
| `GET` が `Unavailable` を返す | `503` |
| `GET` の `If-None-Match` が一致する | `304`。既存リソースには `If-None-Match: *` も一致 |
| `PUT` に `If-Match: <etag>` がある | ETag から復元したバックエンドのリビジョンを指定して更新 |
| `PUT` に `If-None-Match: *` がある | リソースがまだ存在しない場合のみ更新 |
| `PUT` に条件がない | リビジョン検査なしで更新 |
| 更新成功 | `204`。ライターがリビジョンを返した場合は新しい `ETag` も返す |
| ライターが `StateConflictException` を返す | `412` |
| 条件ヘッダーまたはスキーマヘッダーが不正 | `400` |
| 更新時のコンテンツタイプがない、または一致しない | `415` |

null 以外の各バックエンドリビジョンは、`"cfg1.<base64url>"` 形式の強い ETag に変換されます。これにより、16 進数のハッシュのような不透明なリビジョンを HTTP の Entity Tag として扱えます。HTTP クライアントは ETag 文字列をリソースのリビジョンとして返し、`If-Match` または `If-None-Match` で再送します。

条件付きヘッダーで指定できる ETag は 1 つです。読み取りでは 1 つの `If-None-Match` タグまたは `*` を受け付けます。更新では `If-Match` に Configlue の強い ETag を 1 つ指定するか、新規作成の検査に `If-None-Match: *` を使います。更新時に両方の条件は指定できません。複数タグ、弱い書き込みタグ、認識できない書き込みタグには `400` を返します。

スキーマメタデータには `Configlue-Schema-Id` と `Configlue-Schema-Version` を使います。バージョンは正の整数で、モデル ID は省略できます。エンドポイントは読み取ったメタデータをライターに渡し、成功した読み取りの応答にも設定します。

リソースハンドラーには ASP.NET Core の `RequestAborted` トークンを渡します。それ以外のハンドラー例外はアプリケーションの ASP.NET Core エラー処理に任せます。

## HTTP クライアント

末尾のパスプレフィックスを含むエンドポイントルートを使ってリーダーを作成します。パスとメディアタイプはサーバー側の設定と一致させてください。

```csharp
using Configlue.Resource.Http;

var reader = new HttpResourceReader(
    httpClient,
    new Uri("https://config.example/api/settings/"));

ResourceReadResult result = await reader.ReadAsync();
HttpResourceWriter writer = reader.CreateWriter();
```

サーバーでパスを変更した場合は、クライアントにも同じパスを設定します。

```csharp
using Configlue.Resource.Http;

var reader = new HttpResourceReader(
    httpClient,
    new Uri("https://config.example/api/settings/"),
    new HttpResourceOptions
    {
        GetPath = "state/read",
        UpdatePath = "state/write",
    });
```

クライアントでライターを作成しても、サーバー側のアクセスは許可されません。書き込みを許可する場合に限り、更新ルートをマッピングしてください。読み取り専用ソースではリーダーを `SerializedStateSource.FromResource` とコーデックで組み合わせます。書き込み可能なソースでは、リモートエンドポイントが更新に対応する場合だけ `writer: reader.CreateWriter()` を渡します。

クライアントは `404` を `NotFound` に変換し、`408`、`429`、`5xx`、通信エラー、リクエストタイムアウトを `Unavailable` に変換します。それ以外の失敗応答は `HttpRequestException` として通知されます。条件付き更新の失敗（`409` または `412`）は `StateConflictException` になります。
