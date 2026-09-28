---
title: インストール
description: Configlue の SDK 要件と NuGet パッケージ。
---

# インストール

## 要件

* .NET 10 SDK
* ソースジェネレーターを使える言語バージョン（このリポジトリは `preview` でビルドしていますが、各ガイドの `new() { ... }` が記述できれば十分です）

リポジトリ自体のビルドとテストは次のコマンドで実行します。

```sh
dotnet build Configlue.slnx
dotnet test --solution Configlue.slnx --configuration Release
```

## パッケージ

`Configlue` メタパッケージをインストールします。
抽象契約・コアランタイム・DI 統合・JSON プロバイダー・JSON Schema 出力・HTTP リソース・共通レイヤーソース・環境変数ソース・ソースジェネレーターのアナライザーが含まれます。
このパッケージ自体に実装アセンブリはありません。

```bash
dotnet add package Configlue
```

必要に応じて機能パッケージを追加します。

| 用途 | パッケージ |
| --- | --- |
| YAML ファイル | `Configlue.Provider.Yaml` |
| XML ファイル | `Configlue.Provider.Xml` |
| JSON Schema 出力のみ | `Configlue.JsonSchema` |
| `System.CommandLine` 入力 | `Configlue.Source.CommandLine` |
| 共通/ローカル/指定/env プリセット | `Configlue` (`Configlue.Source.Common` を含む) |
| 設定の HTTP 配信 (ASP.NET Core) | `Configlue.Resource.Http.AspNetCore` |
| Dapr state store への永続化 | `Configlue.Resource.Dapr` |
| Amazon S3 object の読み書き | `Configlue.Resource.S3` |
| ZIP アーカイブ内エントリ | `Configlue.Resource.Zip` |
| テスト用インメモリダブル | `Configlue.Testing` |

全一覧と各プロジェクトの役割は [パッケージリファレンス](../reference/packages.md) を参照してください。

## ジェネレーターの動作確認

モデルを宣言してビルドします。
ビルドが成功すれば `Fragment`/`Patch` サポート型が生成されています。

```csharp
using Configlue;

[ConfiglueModel("example.health-check", Version = 1)]
public partial class HealthCheckSettings
{
    public bool Enabled { get; set; } = true;
}
```

モデルには必ず `partial` 修飾子を付与します。
最初のコンストラクター引数は、スキーマ配信や JSON Schema 出力に使う安定したスキーマ ID です。
アプリ内で一意なドット区切り名を付けてください。

次: [クイックスタート](./quick-start.md)。
