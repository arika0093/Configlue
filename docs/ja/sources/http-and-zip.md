---
title: HTTP と ZIP
description: HTTP 越しのリモートポリシーソースとアーカイブ内エントリのリソース化。
---

# HTTP と ZIP

## HTTP リソース

`HttpResourceReader` は `{root}/get` から読み、任意の状態コーデックと組み合わせられます。HTTP リクエストは条件つき書き込みとポーリング (既定5秒間隔) に ETag を使います。

```csharp
using Configlue.Provider.Json;
using Configlue.Resource.Http;

var reader = new HttpResourceReader(httpClient, new Uri("https://config.example/api/settings/"));
model.Sources(sources => sources.FromHttp(new HttpSourceOptions
{
    Id = "policy",
    EndPoint = "https://config.example/api/settings/",
    Client = httpClient,
    Codec = new JsonStateCodec<AppSettings.Fragment>(),
    Priority = 400,
}));
```

HTTP 書き込みは `Writable = true` のときだけ有効で、コーデックの明示が必要です。DI では直接クライアントではなく `ClientFactory = provider => provider!.GetRequiredService<IHttpClientFactory>().CreateClient("settings")` を渡し、ハンドラー寿命をファクトリーに任せます。DI 外では渡したクライアントは呼び出し側所有のままです。

```csharp
sources.FromHttp(new HttpSourceOptions
{
    Id = "remote",
    EndPoint = "https://config.example/api/settings/",
    ClientFactory = provider => provider!.GetRequiredService<IHttpClientFactory>().CreateClient("settings"),
    Codec = new JsonStateCodec<AppSettings.Fragment>(),
    Priority = 400,
    Writable = true,
});
```

エンドポイントが更新に対応するときだけ `writer: reader.CreateWriter()` を `SerializedStateSource.FromResource` に渡します。任意パッケージ `Configlue.Resource.Http.AspNetCore` は同じプロトコルをユーザー提供のリソースハンドラー上にマップします — [HTTP リソースプロトコル](../reference/http-resource-protocol.md) 参照。読みの不達 (`404`) や一時的利用不可はフォールスルー/未検出意味にマップされ、恒久的エラーはアプリに伝わります。

ホストアプリは標準の `AddHttpClient` API で名前つきクライアントを登録し、`FromHttpClientFactory` でファサードソースに渡せます。JSON エンドポイントには `FromJsonHttp`・`FromJsonHttpClientFactory` が JSON コーデックを自動生成します。`Writable = true` はエンドポイントが更新対応のときだけ設定します。これらのソースは既定で読み取り専用です。クライアントは Configlue コンテキスト生成時に解決され、`IHttpClientFactory` が背後のハンドラーを管理し、ソースは返却クライアントを破棄しません。JSON 以外のコーデックには `FromHttp` を使います。

## ZIP エントリ

`ZipEntryResource` はアーカイブ内の1エントリを論理リソースとして公開しつつ、アーカイブの物理同一性とリビジョンを保ちます。互いに重ならないエントリ更新は1回のアーカイブ書き込みにまとめられ、無関係のエントリは無傷です。

## 次のステップ

* [フォールバックと独自ソース](./fallback-and-custom.md)。
* 線上仕様は [HTTP リソースプロトコル](../reference/http-resource-protocol.md)。
