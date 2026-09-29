---
title: JSON Schema とテスト
description: 版つきスキーマの出力とインメモリダブルによるテスト。
---

## JSON Schema 出力

`JsonSchemaGenerator.Generate`・`Write` は独立パッケージ `Configlue.JsonSchema` (名前空間 `Configlue.JsonSchema`) にあり、モデルの生成済み `ConfiglueModelSchema` から版つきスキーマを出力します。`Configlue` メタパッケージにも含まれます。トリミング/NativeAOT 向けのメタデータとしてソース生成の `IJsonTypeInfoResolver` を渡します。対応する DataAnnotations はスキーマ制約に写像されます。

```csharp
var result = JsonSchemaGenerator.Generate(
    [SampleSetting.ConfiglueSchema],
    SampleSettingJsonContext.Default);
```

`Generate` はメモリ上に文書を作り、`Write` は永続化します。生成やファイル出力の診断を確認してください。ファイルの置き場所は自由です — `main` ブランチのフォルダ、CDN、リリース資産など — エディターに指し示してください。

チュートリアル形式で進める場合は [JSON Schema を出力する](../getting-started/08-json-schema.md) を参照してください。

旧来の `--cw-generate-json-schema <directory>` 起動経路を使うには、`ConfiglueBuilder` に登録を集め、アプリを構築する前に `TryWriteFromCommandLine` を呼びます。helper は通常の結果を返し、プロセスの終了方法はホスト側に委ねます:

```csharp
var config = new ConfiglueBuilder();
config.Add<AppSettings>(_ => { /* source と state を登録 */ });

if (JsonSchemaGenerator.TryWriteFromCommandLine(
    args,
    config.ModelSchemas,
    AppJsonContext.Default,
    out var schemaResult))
{
    var generation = schemaResult!;
    foreach (var diagnostic in generation.Diagnostics)
        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");

    return generation.Succeeded ? 0 : 1;
}

using var context = config.CreateContext();
```

option の後に出力ディレクトリが必要です。`--cw-generate-json-schema=schemas` 形式も使えます。DI では command-line 判定後に同じ builder を `services.AddConfiglueBuilder(config)` に渡してください。

`https://example.com/schemas/` のような絶対 URI を `schemaBaseUri` に渡すと、生成スキーマのルート `$id` は URI と版付きファイル名を結合した値になります (例: `https://example.com/schemas/AppSettings.v1.json`)。末尾の `/` は省略できます。query と fragment は指定できません。無効な値は診断 `CWSC012` を返します。既定では生成スキーマはシンプルな保存文書を表し、`$version` と疎な設定項目がルートに並び、モデル ID は保存しません。`DocumentLayout.Detailed` を選ぶと `$configlue`/`$value` エンベロープを使います。null 以外を渡すと、出力スキーマのルートに任意の `$schema` プロパティが加わります。JSON または YAML ファイルソースが書き込むファイルに参照を含めるには、`SchemaReferenceBaseUri` を設定します。writer がモデル別の版つきファイル名を追加します。JSON はルート `$schema` メンバー、YAML は `yaml-language-server` ディレクティブコメントを保存します。Section source はルート文書の形が異なるため、この指定を拒否します。`Write` は引き続きローカルディレクトリにスキーマを書き、公開作業は別途必要です。

## テスト

テストヘルパーは独立パッケージ `Configlue.Testing` にあります:

```shell
dotnet add package Configlue.Testing
```

* `InMemoryResource` — リーダー/ウォッチャー/バッチライターのリソースダブルで `WriteCount` プローブつき。`SerializedStateSource.FromResource` と合成して、ファイルに触らず解決・書き込み・監視をテストします。
* `InMemoryStateStore<T>` — リーダー/ライター/ウォッチャーのダブルで、`Set(value)` による種付け、`SetNotFound()` / `SetUnavailable()` による強制、直接観測ができます。

## 次のステップ

* [バックアップ・ログ・診断](./backups-and-observability.md)。
* [NativeAOT](./native-aot.md)。
