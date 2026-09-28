---
title: Dapr State Management リソース
description: 任意の byte-oriented Configlue 永続化リソースとして Dapr state store を使う。
---

# Dapr State Management リソース

`Configlue.Resource.Dapr` は Dapr State Management の1つの store key を Configlue の byte-resource 契約へ接続します。Dapr を使うアプリケーションだけにインストールしてください:

```sh
dotnet add package Configlue.Resource.Dapr
```

このパッケージは Dapr .NET SDK を使い、設定済みの Dapr state store と実行中の Dapr sidecar を必要とします。`Configlue.Core` と `Configlue` メタパッケージからは参照されません。

キーを typed state source として登録し、シリアライズは Configlue codec に任せます:

```csharp
using Configlue.Provider.Json;
using Configlue.Resource.Dapr;

model.Sources(sources => sources
    .FromDaprState(new DaprStateSourceOptions
    {
        StoreName = "state",
        Key = "project:123",
        Client = daprClient,
        Codec = new JsonStateCodec<AppSettings.Fragment>(),
    })
    .Named("project-state")
    .Writable());
```

返される登録 builder は他 provider と共通の `Named`・`Priority`・`FallbackWhen`・`ReadOnly`・`Writable`・`ExplicitOnly` を持ちます。`Named` はアプリケーションコードからルーティングや移行対象の source を選択する場合に便利です。

DI アプリケーションでは `ClientFactory` からホスト所有の `DaprClient` を解決できます。クライアントの所有権は呼び出し側に残ります。リソースは byte の読み書きを行うだけで、レイヤー、merge、provenance、schema、migration、query/ORM は実装しません。

読み取り時に store が ETag を返せば Configlue revision として公開します。revision を確認する書き込みは Dapr の `FirstWrite` concurrency mode を使って期待 revision を渡し、ETag 不一致を `StateConflictException` に変換します。revision を確認しない書き込みは `LastWrite` を使います。Dapr は書き込み後の新しい ETag を返さないため、書き込み成功時の revision は次回読み取りまで null です。ETag と concurrency の保証は選択した Dapr state-store component に依存します。component が保証しない動作やエラーはそのまま表面化し、強い保証を擬似しません。ETag がない Dapr byte-state 応答では長さ0の値とキー不在を区別できないため、不在として扱います。

変更 watcher とキー間 transaction はありません。設定した state store より強い整合性や transaction を保証しません。

## その他の永続化バックエンド

Dapr State Management の state API に適合するデータベースや key/value store には Dapr を使い、Configlue にデータベースごとの adapter を増やしません。object storage は別の resource 形状です。Amazon S3 には、object key・byte・ETag revision を直接対応づける別パッケージ [`Configlue.Resource.S3`](./s3-object-resource.md) を使ってください。
