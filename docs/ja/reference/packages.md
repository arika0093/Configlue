---
title: パッケージ
description: 機能ごとの NuGet パッケージを探す。
---

| パッケージ | 用途 |
| --- | --- |
| `Configlue` | portable な便利パッケージ: 共通レイヤー/単一バイナリプリセットに加え、DI 登録を含む Core・DI HTTP client adapter・JSON Provider・HTTP・環境変数・Generator をまとめます。AES は opt-in です。 |
| `Configlue.Abstraction` | プロバイダー・コーデック・リソース・生成モデルの契約。 |
| `Configlue.Core` | serializer-neutral な状態解決ランタイム、依存性注入登録、Provider 作成支援、ZIP resource、共通ファイル preset SPI。 |
| `Configlue.Extensions.DI` | 名前付き `IHttpClientFactory` client を使う HTTP source アダプター。 |
| `Configlue.Extensions.MSOptions` | Microsoft options インターフェイス向けの任意アダプター。 |
| `Configlue.Extensions.R3` | value・profile・reload signal の合成に使う任意の R3 observable。 |
| `Configlue.Generator` | 疎モデル生成サポート (Roslyn アナライザー)。 |
| `Configlue.Testing` | インメモリリソースとテストダブル。 |
| `Configlue.Provider.Json` | JSON コーデック、セクションリソース、ファイル登録、共通 preset の JSON 選択。 |
| `Configlue.JsonSchema.MSBuild` | Configlue モデルからビルド時に JSON Schema を生成。直接参照専用の開発依存で、`Configlue` メタパッケージには含まれません。 |
| `Configlue.Provider.Xml` | セクションリソースとファイル登録、共通 preset の XML 選択つき XML コーデック。 |
| `Configlue.Provider.Yaml` | セクションリソースとファイル登録、共通 preset の YAML 選択つき YAML コーデック。 |
| `Configlue.Source.Environment` | プロセス環境変数に支えられた読み取り専用ソース。 |
| `Configlue.Source.CommandLine` | `System.CommandLine` パース結果に支えられた読み取り専用ソース。共通プリセットへの任意追加にも対応します。 |
| `Configlue.Resource.Http` | ETag リビジョンとポーリング変更検出つき HTTP 読み書きリソース。 |
| `Configlue.Resource.Http.AspNetCore` | HTTP リソース配信の ASP.NET Core エンドポイント。 |
| `Configlue.Extensions.AspNetCore` | ASP.NET Core の現在リクエストから subject を解決する統合。 |
| `Configlue.Extensions.Blazor` | Blazor 認証状態からの subject 解決と circuit 単位の変更通知。 |
| `Configlue.Resource.WebStorage` | ブラウザーの `localStorage` / `sessionStorage` リソース。DI スコープ単位で生成されます。 |
| `Configlue.Resource.S3` | ETag revision を使う Amazon S3 object resource と source 登録。 |
| `Configlue.Resource.PostgreSql` | subject key ごとの row、revision の atomic check、`LISTEN`/`NOTIFY` watcher を備えた任意の PostgreSQL byte resource。 |
| `Configlue.Resource.Redis` | subject key ごとの key、Lua による revision の atomic check、Pub/Sub invalidation を備えた任意の Redis byte resource。 |
| `Configlue.Transformer.AES` | Resource と Codec の間で state bytes を AES-GCM 暗号化・認証。パスフレーズからの鍵導出にも対応します。 |

## Target Framework と直接依存

| パッケージ | Assets | 直接 NuGet 依存と最低 TFM |
| --- | --- | --- |
| `Configlue.Abstraction` | `netstandard2.0;netstandard2.1;net10.0` | `netstandard2.0` のみ `System.Memory` 4.6.3 と `Microsoft.Bcl.AsyncInterfaces` 10.0.5。`netstandard2.1` asset は互換パッケージを使わずプラットフォームの async iterator を利用します。 |
| `Configlue.Core` | `netstandard2.0;netstandard2.1;net10.0` | `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.0、`Microsoft.Extensions.Logging.Abstractions` 10.0.0、`System.IO.Hashing` 10.0.0、`System.IO.Pipelines` 10.0.0。`System.ComponentModel.Annotations` 5.0.0 と `System.Threading.Channels` 10.0.5 は両 Standard asset。`Microsoft.Bcl.AsyncInterfaces` 10.0.5 と `System.Threading.Tasks.Extensions` 4.6.3 は `netstandard2.0` のみ。Core は `System.Text.Json` に依存しません。 |
| `Configlue` | `netstandard2.0;netstandard2.1;net10.0` | 直接 NuGet 依存なし。project reference で portable な Core/DI/JSON/HTTP/環境変数グラフを構成します。AES は参照しません。 |
| `Configlue.Extensions.DI` | `netstandard2.0;netstandard2.1;net10.0` | `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.0、`Microsoft.Extensions.Http` 10.0.0。 |
| `Configlue.Extensions.MSOptions` | `netstandard2.0;netstandard2.1;net10.0` | `Microsoft.Extensions.Options` 10.0.0。 |
| `Configlue.Extensions.R3` | `netstandard2.0;netstandard2.1;net10.0` | `R3` 1.3.1。 |
| `Configlue.Generator` | `netstandard2.0` | `Microsoft.CodeAnalysis.CSharp` 4.11.0、`Microsoft.CodeAnalysis.Analyzers` 3.11.0 (analyzer 内部依存)。 |
| `Configlue.Testing` | `netstandard2.0;netstandard2.1;net10.0` | 直接 NuGet 依存なし。 |
| `Configlue.Provider.Json` | `netstandard2.0;netstandard2.1;net10.0` | `System.IO.Pipelines` 10.0.0。`System.Text.Json` 10.0.0 は両 Standard asset。 |
| `Configlue.JsonSchema.MSBuild` | `build/` と `tasks/net10.0` (`lib` asset なし) | 開発専用のビルドツール。固定した `JsonSchema.Net` 生成スタックを `tasks/net10.0` に同梱し、伝播する NuGet 依存を宣言しません。 |
| `Configlue.Provider.Xml` | `netstandard2.0;netstandard2.1;net10.0` | 直接 NuGet 依存なし。 |
| `Configlue.Provider.Yaml` | `netstandard2.0;netstandard2.1;net10.0` | `SharpYaml` 3.13.1。 |
| `Configlue.Source.Environment` | `netstandard2.0;netstandard2.1;net10.0` | `System.Text.Json` 10.0.0 は両 Standard asset。 |
| `Configlue.Source.CommandLine` | `netstandard2.0;netstandard2.1;net10.0` | `System.CommandLine` 2.0.12。 |
| `Configlue.Resource.Http` | `netstandard2.0;netstandard2.1;net10.0` | 直接 NuGet 依存なし。 |
| `Configlue.Resource.Http.AspNetCore` | `net10.0` | `Microsoft.AspNetCore.App` framework reference。 |
| `Configlue.Extensions.AspNetCore` | `net10.0` | `Microsoft.AspNetCore.App` framework reference。 |
| `Configlue.Extensions.Blazor` | `net10.0` | `Microsoft.AspNetCore.App` framework reference。 |
| `Configlue.Resource.WebStorage` | `net10.0` | `Microsoft.JSInterop` 10.0.0。 |
| `Configlue.Resource.S3` | `netstandard2.0;netstandard2.1;net10.0` | `AWSSDK.S3` 4.0.103.4。 |
| `Configlue.Resource.PostgreSql` | `net8.0;net10.0` | `Npgsql` 10.0.3 の最低 TFM により `net8.0` 以上。 |
| `Configlue.Resource.Redis` | `netstandard2.0;netstandard2.1;net10.0` | `StackExchange.Redis` 3.3.1。 |
| `Configlue.Transformer.AES` | `netstandard2.1;net10.0` | 直接 NuGet 依存なし。AES-GCM 要件により最低 TFM は `netstandard2.1`。AES 拡張を使う場合は明示的に追加してください。 |

Solution build には、生成モデルを使う consumer fixture が両 Standard family (`netstandard2.0` と `netstandard2.1`) 向けに JSON Provider あり/なしの両方で含まれます。CI では generator assembly 自体とは別に、consumer 側の互換性もビルドします。

パッケージ参照と版の正本はプロジェクトファイルです。任意プロバイダーは必要になったら直接インストールしてください。まずは [インストール](../getting-started/installation.md) からどうぞ。

object storage は [Amazon S3 object resource](./s3-object-resource.md)を参照してください。
PostgreSQL への直接保存は [PostgreSQL resource](./postgresql-resource.md)を参照してください。
Redis への直接保存は [Redis resource](./redis-resource.md)を参照してください。
