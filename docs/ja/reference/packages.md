---
title: パッケージ
description: 機能ごとの NuGet パッケージを探す。
---

# パッケージ

| パッケージ | 用途 |
| --- | --- |
| `Configlue` | ユーザー向けメタパッケージ: Core・DI 統合・JSON プロバイダー・JSON Schema 出力・HTTP リソース・共通レイヤーソース・環境変数ソース・ジェネレーターアナライザー。実装アセンブリ自体はありません。 |
| `Configlue.Abstraction` | プロバイダー・コーデック・リソース・生成モデルの契約。 |
| `Configlue.Core` | 状態解決と永続化ランタイム。 |
| `Configlue.Extensions.DI` | Configlue options の依存性注入登録。 |
| `Configlue.Extensions.MSOptions` | Microsoft options インターフェイス向けの任意アダプター。 |
| `Configlue.Generator` | 疎モデル生成サポート (Roslyn アナライザー)。 |
| `Configlue.Testing` | インメモリリソースとテストダブル。 |
| `Configlue.Provider.Json` | JSON コーデック、セクションリソース、ファイル登録。 |
| `Configlue.JsonSchema` | Configlue モデルから JSON Schema を生成・出力。`Configlue` メタパッケージに含まれます。 |
| `Configlue.Provider.Xml` | セクションリソースとファイル登録つき XML コーデック。 |
| `Configlue.Provider.Yaml` | セクションリソースとファイル登録つき YAML コーデック。 |
| `Configlue.Source.Environment` | プロセス環境変数に支えられた読み取り専用ソース。 |
| `Configlue.Source.CommandLine` | `System.CommandLine` パース結果に支えられた読み取り専用ソース。共通プリセットへの任意追加にも対応します。 |
| `Configlue.Source.Presets` | 共通レイヤーと単一バイナリのソースプリセット。`Configlue` メタパッケージに含まれます。 |
| `Configlue.Source.Presets.Xml` | 共通プリセットのファイル層で XML Provider を使う任意アダプター。 |
| `Configlue.Source.Presets.Yaml` | 共通プリセットのファイル層で YAML Provider を使う任意アダプター。 |
| `Configlue.Resource.Http` | ETag リビジョンとポーリング変更検出つき HTTP 読み書きリソース。 |
| `Configlue.Resource.Http.AspNetCore` | HTTP リソース配信の ASP.NET Core エンドポイント。 |
| `Configlue.Resource.Dapr` | Dapr State Management 向けの任意 byte resource と source 登録。 |
| `Configlue.Resource.S3` | ETag revision を使う Amazon S3 object resource と source 登録。 |
| `Configlue.Resource.Zip` | ZIP アーカイブ内1エントリのリソースビュー。 |
| `Configlue.Transformer.AES` | Resource と Codec の間で state bytes を AES-GCM 暗号化・認証。パスフレーズからの鍵導出にも対応します。 |

パッケージ参照と版の正本はプロジェクトファイルです。任意プロバイダーは必要になったら直接インストールしてください。まずは [インストール](../getting-started/installation.md) からどうぞ。

Dapr state 永続化と object storage は、[Dapr State Management リソース](./dapr-state-resource.md)および [Amazon S3 object resource](./s3-object-resource.md)を参照してください。
