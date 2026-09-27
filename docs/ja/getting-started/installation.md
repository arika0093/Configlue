---
title: インストール
description: Configlue の SDK 要件と NuGet パッケージ。
---

# インストール

## 要件

* .NET 10 SDK。
* ソースジェネレーターを使える言語バージョン (このリポジトリは `preview` でビルドしていますが、各ガイドの `new() { ... }` が書ければ十分です)。

リポジトリ自体のビルドとテストは:

```sh
dotnet build Configlue.slnx
dotnet test --solution Configlue.slnx --configuration Release
```

## パッケージ

`Configlue` メタパッケージをインストールします。抽象契約・コアランタイム・DI 統合・JSON プロバイダー・HTTP リソース・共通レイヤーソース・環境変数ソース・ソースジェネレーターのアナライザーが入ります。このパッケージ自体に実装アセンブリはありません。

```bash
dotnet add package Configlue
```

必要に応じて機能パッケージを追加します:

| 用途 | パッケージ |
| --- | --- |
| YAML ファイル | `Configlue.Provider.Yaml` |
| XML ファイル | `Configlue.Provider.Xml` |
| `System.CommandLine` 入力 | `Configlue.Source.CommandLine` |
| 共通/ローカル/指定/env プリセット | `Configlue` (`Configlue.Source.Common` を含む) |
| 設定の HTTP 配信 (ASP.NET Core) | `Configlue.Resource.Http.AspNetCore` |
| ZIP アーカイブ内エントリ | `Configlue.Resource.Zip` |
| テスト用インメモリダブル | `Configlue.Testing` |

全一覧と各プロジェクトの役割は [パッケージリファレンス](../reference/packages.md) にあります。

## ジェネレーターの動作確認

モデルを宣言してビルドします。成功すれば `Fragment`/`Patch` サポート型が生成されています。

```csharp
using Configlue;

[ConfiglueModel("example.health-check", Version = 1)]
public partial class HealthCheckSettings
{
    public bool Enabled { get; set; } = true;
}
```

モデルは必ず `partial` にします。最初のコンストラクター引数はスキーマ配送や JSON Schema 出力に使う安定したスキーマ ID です。アプリ内で一意なドット区切り名を付けてください。

次: [クイックスタート](./quick-start.md)。
