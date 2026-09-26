---
title: HTTP リソースプロトコル
description: Configlue の HTTP リソースが使うエンドポイント、リビジョン、書き込み、ポーリングの仕様です。
---

# HTTP リソースプロトコル

Configlue の HTTP リソースはシリアライズ済みのリソースデータを転送します。特定のコーデックやモデル型は要求しません。`SerializedStateSource.FromResource` を通じて JSON、YAML、XML、独自の状態コーデックと組み合わせます。

## エンドポイント

エンドポイントのルートはベース URI です。既定のパスは `GET {root}/get` と `PUT {root}/update` です。`HttpResourceOptions` で相対パスを変更できます。パスはルート URI の配下に置く必要があります。

`HttpClient` は呼び出し側が用意します。認証情報、既定のリクエストヘッダー、証明書、プロキシ、タイムアウトをそのクライアントに設定してください。Configlue は `HttpClient` を所有せず、破棄もしません。

## 読み取り

`GET {root}/get` はステータス `200` とリソースのバイト列を返します。レスポンスの `ETag` ヘッダーは条件付き書き込みと効率的なポーリングに使う不透明なリビジョンです。`ETag` がない場合も内容のフィンガープリントを比較してポーリングできますが、既存リソースの条件付き置き換えはできません。

任意のレスポンスヘッダー `Configlue-Schema-Id` と `Configlue-Schema-Version` は `StateSchemaMetadata` を運びます。どちらかが指定される場合、`Configlue-Schema-Version` にはカルチャに依存しない正の整数を指定してください。スキーマ識別子は省略できます。

レスポンスの扱いは次のとおりです。

- `404` は `NotFound` を返します。
- `408`、`429`、`5xx` は `Unavailable` を返します。
- ネットワーク障害とクライアント側のタイムアウトは `Unavailable` を返します。
- `401`、`403`、その他の `4xx`、`HttpClient` が追従しないリダイレクト、不正なレスポンスは例外になります。
- `304` は条件付きポーリングリクエストでのみ使います。

## 書き込み

`PUT {root}/update` がシリアライズ済みのバイト列を受け取ります。既定の Content-Type は `application/octet-stream` です。エンドポイントの仕様に合わせるには `HttpResourceOptions.ContentType` を設定します。

書き込みに期待リビジョンがある場合、Configlue は強い ETag を `If-Match` に設定します。リソースが存在しないことを明示的に確認する場合は `If-None-Match: *` を設定します。条件が一致しないとき、エンドポイントは `412 Precondition Failed` を返す必要があります。Configlue は `412` と `409 Conflict` を `StateConflictException` に変換します。

成功時は任意の `2xx` レスポンスを返します。結果の `ETag` を含めることを推奨します。レスポンスボディは無視されます。任意のスキーマメタデータは `Configlue-Schema-Id` と `Configlue-Schema-Version` のリクエストヘッダーで送信します。

## ポーリング

`HttpResourceReader` は `IStateWatcher` を実装します。`HttpResourceOptions.PollingInterval` ごとに確認し、既定の間隔は 5 秒です。ETag を受け取っている場合、ポーリングに `If-None-Match` を付け、`304` を変更なしとして扱います。ETag がない場合はリソースの状態と内容のフィンガープリントを比較します。呼び出し側のキャンセルにより待機中の処理と実行中の HTTP リクエストを停止できます。

ウォッチャーは変更を無効化として通知します。通知の後、Configlue ランタイムがリソースを再読み込みします。
