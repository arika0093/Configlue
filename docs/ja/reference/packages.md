---
title: パッケージ
description: 機能ごとの NuGet パッケージを探す。
---

# パッケージ

| パッケージ | 用途 |
| --- | --- |
| `Configlue` | ユーザー向けメタパッケージ: Core・DI 統合・JSON プロバイダー・HTTP リソース・環境変数ソース・ジェネレーターアナライザー。実装アセンブリ自体はありません。 |
| `Configlue.Abstraction` | プロバイダー・コーデック・リソース・生成モデルの契約。 |
| `Configlue.Core` | 状態解決と永続化ランタイム。 |
| `Configlue.Extensions.DI` | Configlue options の依存性注入登録。 |
| `Configlue.Extension.MSOptions` | Microsoft options インターフェイス向けの任意アダプター。 |
| `Configlue.Generator` | 疎モデル生成サポート (Roslyn アナライザー)。 |
| `Configlue.Testing` | インメモリリソースとテストダブル。 |
| `Configlue.Provider.Json` | JSON コーデック、セクションリソース、ファイル登録、JSON Schema 出力。 |
| `Configlue.Provider.Xml` | セクションリソースとファイル登録つき XML コーデック。 |
| `Configlue.Provider.Yaml` | セクションリソースとファイル登録つき YAML コーデック。 |
| `Configlue.Source.Environment` | プロセス環境変数に支えられた読み取り専用ソース。 |
| `Configlue.Source.CommandLine` | `System.CommandLine` パース結果に支えられた読み取り専用ソース。 |
| `Configlue.Source.Common` | 共通/ローカル/ファイル/環境変数/コマンドラインのソースプリセット。 |
| `Configlue.Resource.Http` | ETag リビジョンとポーリング変更検出つき HTTP 読み書きリソース。 |
| `Configlue.Resource.Http.AspNetCore` | HTTP リソース配信の ASP.NET Core エンドポイント。 |
| `Configlue.Resource.Zip` | ZIP アーカイブ内1エントリのリソースビュー。 |

パッケージ参照と版の正本はプロジェクトファイルです。任意プロバイダーは必要になったら直接インストールしてください。まずは [インストール](../getting-started/installation.md) からどうぞ。
