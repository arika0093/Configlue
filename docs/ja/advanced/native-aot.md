---
title: NativeAOT
description: ソース生成されたシリアル化メタデータによるトリム安全な構成。
---

ソース生成された JSON メタデータとフラグメントスキーマで、Configlue は NativeAOT 環境で動きます。コーデックに `JsonSerializerContext` を渡し、YAML には生成されたシリアライザーオプションとフラグメントスキーマを渡します。

```csharp
// JSON: ソース生成メタデータでコーデックをトリム安全に。
new JsonStateCodec<SampleSetting.Fragment>(SampleSettingJsonContext.Default.SampleSettingFragment)
```

YAML にはモデル・フラグメント・スカラー型を網羅する `YamlSerializerContext` を生成し、起動時にラウンドトリップ検証します。完全な手順は `example/Example.ConsoleApp.NativeAot` にあります。`PublishAot=true` で発行し、保存モデルを `StateSourceProjection.Project` で疎フラグメントに投影し、コンテキスト生成前に YAML コーデックを検査します:

```sh
dotnet publish example/Example.ConsoleApp.NativeAot --configuration Release --runtime linux-x64 --self-contained true
```

トリムするアプリではリフレクションベースのオーバーロードより `JsonTypeInfo` ベースのコンストラクター ([旧来デコーダー](../migration/adopting-configuration-writable.md) も同様) を使ってください。JSON Schema 生成はビルド時の関心事であり、トリミングには影響しません — [JSON Schema とテスト](./json-schema-and-testing.md) 参照。

`XmlStateCodec` は `XmlSerializer` のリフレクションと実行時コード生成を使います。コーデックメソッドにはトリミングと動的コードの要件を注釈し、トリム対象や NativeAOT アプリケーションから呼ぶとアナライザー警告が出るようにしています。現在、XML の生成コード経路はありません。

チュートリアル形式で進める場合は [NativeAOT で配布する](../getting-started/10-native-aot.md) を参照してください。

## 次のステップ

* [JSON Schema とテスト](./json-schema-and-testing.md)。
* [サンプル集](../getting-started/examples.md)。
