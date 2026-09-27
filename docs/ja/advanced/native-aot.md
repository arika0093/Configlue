---
title: NativeAOT
description: ソース生成されたシリアル化メタデータによるトリム安全な構成。
---

# NativeAOT

ソース生成された JSON メタデータとフラグメントスキーマで、Configlue は NativeAOT 環境で動きます。コーデックに `JsonSerializerContext` を渡し、YAML には生成されたシリアライザーオプションとフラグメントスキーマを渡します。

```csharp
// JSON: ソース生成メタデータでコーデックをトリム安全に。
new JsonStateCodec<SampleSetting.Fragment>(SampleSettingJsonContext.Default.SampleSettingFragment)
```

YAML にはモデル・フラグメント・スカラー型を網羅する `YamlSerializerContext` を生成し、起動時にラウンドトリップ検証します。完全な手順は `example/Example.ConsoleApp.NativeAot` にあります。`PublishAot=true` で発行し、保存モデルを `StateSourceProjection.Project` で疎フラグメントに投影し、コンテキスト生成前に YAML コーデックを検査します:

```sh
dotnet publish example/Example.ConsoleApp.NativeAot --configuration Release --runtime linux-x64 --self-contained true
```

トリムするアプリではリフレクションベースのオーバーロードより `JsonTypeInfo` ベースのコンストラクター ([旧来デコーダー](../migration/adopting-configuration-writable.md) も同様) を使ってください。`JsonSchemaGenerator.Generate` もソース生成の `IJsonTypeInfoResolver` を取ります — [JSON Schema とテスト](./json-schema-and-testing.md) 参照。

## 次のステップ

* [JSON Schema とテスト](./json-schema-and-testing.md)。
* [サンプル集](../getting-started/examples.md)。
