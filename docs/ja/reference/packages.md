---
title: パッケージ
description: 機能ごとの NuGet パッケージを探す。
---

| パッケージ | 用途 |
| --- | --- |
| `Configlue` | batteries-included パッケージ: 共通レイヤー/単一バイナリプリセットに加え、Core・DI 統合・JSON Provider・JSON Schema・HTTP・環境変数・AES・Generator をまとめます。 |
| `Configlue.Abstraction` | プロバイダー・コーデック・リソース・生成モデルの契約。 |
| `Configlue.Core` | serializer-neutral な状態解決ランタイム、Provider 作成支援、ZIP resource、共通ファイル preset SPI。 |
| `Configlue.Extensions.DI` | Configlue options の依存性注入登録。 |
| `Configlue.Extensions.MSOptions` | Microsoft options インターフェイス向けの任意アダプター。 |
| `Configlue.Generator` | 疎モデル生成サポート (Roslyn アナライザー)。 |
| `Configlue.Testing` | インメモリリソースとテストダブル。 |
| `Configlue.Provider.Json` | JSON コーデック、セクションリソース、ファイル登録、共通 preset の JSON 選択。 |
| `Configlue.JsonSchema` | Configlue モデルから JSON Schema を生成・出力。`Configlue` メタパッケージに含まれます。 |
| `Configlue.Provider.Xml` | セクションリソースとファイル登録、共通 preset の XML 選択つき XML コーデック。 |
| `Configlue.Provider.Yaml` | セクションリソースとファイル登録、共通 preset の YAML 選択つき YAML コーデック。 |
| `Configlue.Source.Environment` | プロセス環境変数に支えられた読み取り専用ソース。 |
| `Configlue.Source.CommandLine` | `System.CommandLine` パース結果に支えられた読み取り専用ソース。共通プリセットへの任意追加にも対応します。 |
| `Configlue.Resource.Http` | ETag リビジョンとポーリング変更検出つき HTTP 読み書きリソース。 |
| `Configlue.Resource.Http.AspNetCore` | HTTP リソース配信の ASP.NET Core エンドポイント。 |
| `Configlue.Resource.Dapr` | ETag concurrency check つき Dapr State Management resource と source 登録。 |
| `Configlue.Resource.S3` | ETag revision を使う Amazon S3 object resource と source 登録。 |
| `Configlue.Transformer.AES` | Resource と Codec の間で state bytes を AES-GCM 暗号化・認証。パスフレーズからの鍵導出にも対応します。 |

パッケージ参照と版の正本はプロジェクトファイルです。任意プロバイダーは必要になったら直接インストールしてください。まずは [インストール](../getting-started/installation.md) からどうぞ。

object storage は [Amazon S3 object resource](./s3-object-resource.md)を参照してください。
