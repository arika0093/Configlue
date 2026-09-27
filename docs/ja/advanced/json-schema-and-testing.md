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

## テスト

テストヘルパーは独立パッケージ `Configlue.Testing` にあります:

```shell
dotnet add package Configlue.Testing
```

* `InMemoryResource` — リーダー/ウォッチャー/バッチライターのリソースダブルで `WriteCount` プローブつき。`SerializedStateSource.FromResource` と合成して、ファイルに触らず解決・書き込み・監視をテストします。
* `InMemoryStateStore<T>` — リーダー/ライター/ウォッチャーのダブルで、`Set(value)` による種付け、`SetNotFound()` / `SetUnavailable()` による強制、直接観測ができます。

## 次のステップ

* [バックアップと可観測性](./backups-and-observability.md)。
* [NativeAOT](./native-aot.md)。
