---
title: JSON Schema とテスト
description: 版つきスキーマの出力とインメモリダブルによるテスト。
---

# JSON Schema とテスト

## JSON Schema 出力

`JsonSchemaGenerator.Generate`・`Write` はモデルの生成済み `ConfiglueModelSchema` から版つきスキーマを出力します。トリミング/NativeAOT 向けのメタデータとしてソース生成の `IJsonTypeInfoResolver` を渡します。対応する DataAnnotations はスキーマ制約に写像されます。

```csharp
var result = JsonSchemaGenerator.Generate(
    [SampleSetting.ConfiglueModelSchema],
    SampleSettingJsonContext.Default);
```

`Generate` はメモリ上に文書を作り、`Write` は永続化します。結果の診断を確認してください (`CWSC001` は `System.Text.Json` のスキーマ出力に対応しない framework の報告です)。ファイルの置き場所は自由です — `main` ブランチのフォルダ、CDN、リリース資産など — エディターに指し示してください。

`https://example.com/schemas/` のような絶対 URI を `schemaBaseUri` に渡すと、生成スキーマのルート `$id` は URI と版付きファイル名を結合した値になります (例: `https://example.com/schemas/AppSettings.v1.json`)。末尾の `/` は省略できます。query と fragment は指定できません。無効な値は診断 `CWSC012` を返します。生成スキーマは保存文書のエンベロープを表し、`$configlue` がモデル ID と版を、`$value` が疎な設定フラグメントを持ちます。null 以外を渡すと、出力スキーマのルートに任意の `$schema` プロパティが加わります。JSON または YAML ファイルソースが書き込むファイルに参照を含めるには、`SchemaReferenceBaseUri` を設定します。writer がモデル別の版つきファイル名を追加します。JSON はルート `$schema` メンバー、YAML は `yaml-language-server` ディレクティブコメントを保存します。Section source はルート文書の形が異なるため、この指定を拒否します。`Write` は引き続きローカルディレクトリにスキーマを書き、公開作業は別途必要です。

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
