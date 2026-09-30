---
title: インストール
description: Configlue を使い始めるための要件とパッケージ。
---

## 必要な環境

Configlue は現在 .NET 10 を対象にしています。アプリケーションを作成する前に .NET 10 SDK をインストールしてください。

コンソールアプリを新しく作る場合は、次のコマンドから始められます。

```sh
dotnet new console -n ConfiglueTutorial
cd ConfiglueTutorial
dotnet add package Configlue
```

`Configlue` メタパッケージには、コアランタイム、JSON プロバイダー、共通ソースのプリセット、環境変数、DI 統合、JSON Schema、ソースジェネレーターが含まれます。

## 必要に応じて追加するパッケージ

アプリケーションで使う機能だけを追加します。

| 用途 | パッケージ |
| --- | --- |
| YAML ファイル | `Configlue.Provider.Yaml` |
| XML ファイル | `Configlue.Provider.Xml` |
| 共通プリセットで YAML を使う | `Configlue.Source.Presets.Yaml` |
| 共通プリセットで XML を使う | `Configlue.Source.Presets.Xml` |
| `System.CommandLine` から読む | `Configlue.Source.CommandLine` |
| Microsoft `IOptions<T>` と接続する | `Configlue.Extensions.MSOptions` |
| Rx.NET と接続する | `Configlue.Extensions.Reactive` |
| R3 と接続する | `Configlue.Extensions.R3` |
| ASP.NET Core 統合 (リクエスト subject と HTTP リソース配信) | `Configlue.Extensions.AspNetCore` |
| Blazor 統合 (認証状態 subject とブラウザー storage) | `Configlue.Extensions.Blazor` |
| Amazon S3 | `Configlue.Resource.S3` |
| PostgreSQL への直接保存 | `Configlue.Resource.PostgreSql` |
| Redis への直接保存 | `Configlue.Resource.Redis` |
| ZIP アーカイブ | `Configlue.Resource.Zip` |
| AES-GCM 暗号化 | `Configlue.Transformer.AES` |
| テスト用のインメモリ実装 | `Configlue.Testing` |
| 独自プロバイダーの開発 | `Configlue.Extensibility` |

パッケージ全体の対応関係は [パッケージ一覧](../reference/packages.md) を参照してください。

## ソースジェネレーターを確認する

Configlue のモデルは `[ConfiglueModel]` を付けた `partial` クラスとして定義します。ビルド時に、疎な状態を表す Fragment、書き込み用の Patch、値の出所を調べる Details が生成されます。

```csharp
using Configlue;

[ConfiglueModel("example.health-check", Version = 1)]
public partial class HealthCheckSettings
{
    public bool Enabled { get; set; } = true;
}
```

モデル ID は保存データのスキーマを識別する値です。設定ファイルを配布した後は同じ ID を使い続けてください。

次: [クイックスタート](./quick-start.md)。
