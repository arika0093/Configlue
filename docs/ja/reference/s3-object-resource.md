---
title: Amazon S3 object resource
description: 任意の byte-oriented Configlue resource として Amazon S3 object を使う。
---

# Amazon S3 object resource

`Configlue.Resource.S3` は S3 object 1つを Configlue の byte-resource 契約へ接続します。S3 を使うアプリケーションだけにインストールしてください:

```sh
dotnet add package Configlue.Resource.S3
```

このパッケージは .NET 用 AWS SDK の S3 client に依存し、`Configlue.Core` と `Configlue` メタパッケージからは参照されません。credentials、region、endpoint は AWS SDK client 側で設定してください。

object を typed state source として登録し、シリアライズは Configlue codec に任せます:

```csharp
using Configlue.Provider.Json;
using Configlue.Resource.S3;

model.Sources(sources => sources.FromS3Object(new S3ObjectSourceOptions
{
    BucketName = "app-config",
    Key = "production/settings.json",
    Client = s3Client,
    Codec = new JsonStateCodec<AppSettings.Fragment>(),
}));
```

DI アプリケーションでは `ClientFactory` からホスト所有の `IAmazonS3` を解決できます。client の所有権は呼び出し側に残ります。resource が必要な場所では `S3ObjectResource` を直接利用できます。

読み取りは object の byte と ETag を返します。revision check を要求した書き込みでは、期待 ETag による S3 conditional `If-Match`、または object 不在を期待する場合は `If-None-Match: *` を使います。S3 precondition failure は `StateConflictException` になります。無条件書き込みはそのまま無条件です。object 不在は NotFound として返し、認証、bucket、network その他の S3 error は表面化します。

ETag は不透明な S3 revision であり、常に content hash とは限りません (multipart upload や暗号化などの影響を受けます)。条件付き書き込みの保証には、利用する S3 実装がこれらの request header をサポートする必要があります。この provider は change watcher、object 間 transaction、bucket 管理を実装しません。
