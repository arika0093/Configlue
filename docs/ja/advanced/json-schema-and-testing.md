---
title: JSON Schema とテスト
description: 版つきスキーマの出力とインメモリダブルによるテスト。
---

## JSON Schema 出力

JSON Schema 生成はビルド時パッケージ `Configlue.JsonSchema.MSBuild` が担います。アプリケーションと `Configlue` メタパッケージにはランタイムのスキーマ生成 API も `JsonSchema.Net` 依存もありません。パッケージはビルド後のアセンブリから `[ConfiglueModel]` 型をリフレクションで検出し、`Build` 後にスキーマを書き出します。

```shell
dotnet add package Configlue.JsonSchema.MSBuild
```

パッケージを参照するだけで、アプリケーションコードなしに生成が有効になります。`ConfiglueGenerateSchemas` を `false` にすると無効化でき、ツールや CI 向けには `GenerateConfiglueSchemas` ターゲットを明示的に使えます:

```shell
dotnet msbuild -t:GenerateConfiglueSchemas
```

チュートリアル形式で進める場合は [JSON Schema を出力する](../getting-started/08-json-schema.md) を参照してください。

### 出力とレイアウト

既定の出力ルートは次の順で解決されます:

1. 明示された `ConfiglueSchemaOutputPath`
2. 利用可能なら `$(SolutionDir)/schemas`
3. `.sln` または `.slnx` を含む最も近い親ディレクトリの `schemas`
4. `$(MSBuildProjectDirectory)/schemas`

スキーマはマルチターゲットでもプロジェクトビルドごとに一度だけ書き出されます (`ConfiglueSchemaTargetFramework` が検査対象フレームワークを選び、既定は先頭)。内容が変わっていないファイルは再書き込みされません。

`https://example.com/schemas/` のような絶対 URI を `ConfiglueSchemaBaseUri` に渡すと、生成スキーマのルート `$id` は URI と版付きファイル名を結合した値になります (例: `https://example.com/schemas/AppSettings.v1.json`)。末尾の `/` は省略できます。query と fragment は指定できません。既定では生成スキーマはシンプルな保存文書を表し、`$version` と任意の設定項目がルートに並び、モデル ID は保存しません。`$configlue`/`$value` エンベロープが必要な場合は `ConfiglueSchemaDocumentLayout` に `Detailed` を、バージョンプロパティ名を変える場合は `ConfiglueSchemaVersionProperty` を設定します。base URI を指定すると、出力スキーマのルートに任意の `$schema` プロパティが加わります。

JSON または YAML ファイルソースが書き込むファイルに参照を含めるには、`SchemaReferenceBaseUri` を設定します。writer がモデル別の版つきファイル名を追加します。JSON はルート `$schema` メンバー、YAML は `yaml-language-server` ディレクティブコメントを保存します。Section source はルート文書の形が異なるため、この指定を拒否します。MSBuild パッケージはローカルディレクトリにスキーマを書き、公開作業は別途必要です。

### 依存バージョンポリシー

スキーマツールは `JsonSchema.Net` 生成スタックを承認済みの pre-OSMF バージョンに固定します。参照バージョン、承認済みバージョンポリシー (`AllowedJsonSchemaNetVersion` など)、`packages.lock.json` はそれぞれ独立に検証されるため、承認済みの依存境界を越える更新はポリシーを意図的に変更しない限り通常のビルドで失敗します。

## テスト

テストヘルパーは独立パッケージ `Configlue.Testing` にあります:

```shell
dotnet add package Configlue.Testing
```

* `InMemoryResource` — リーダー/ウォッチャー/バッチライターのリソースダブルで `WriteCount` プローブつき。`SerializedStateSource.FromResource` と合成して、ファイルに触らず解決・書き込み・監視をテストします。
* `InMemoryStateSource<T>` — リーダー/ライター/ウォッチャーのダブルで、`Set(value)` による種付け、`SetNotFound()` / `SetUnavailable()` による強制、直接観測ができます。

## 次のステップ

* [バックアップ・ログ・診断](./backups-and-observability.md)。
* [NativeAOT](./native-aot.md)。
