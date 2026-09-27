---
title: "STEP 7: JSON Schema を出力する"
description: 生成モデルからスキーマを出し、エディター補完とCI検査に使う。
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 7: JSON Schema を出力する

設定ファイルの書き間違いは、実行前に気づきたいものです。Configlue は生成モデルの `ConfiglueModelSchema` から JSON Schema を出せます。エディターの補完や CI の検査に使ってください。

スキーマ生成はビルド時に使う機能で、アプリが DI を使うかどうかには依存しません。

## スキーマを書き出す

```csharp
using System.Text.Json.Serialization;
using Configlue.Provider.Json;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppSettings.Fragment))]
internal partial class AppJsonContext : JsonSerializerContext;
```

呼び出しは DI の有無にかかわらず同じです。

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
var result = JsonSchemaGenerator.Write<AppSettings, AppSettings.Fragment>(
    "schemas",
    AppJsonContext.Default);
Console.WriteLine($"書き出し: {string.Join(", ", result.WrittenFiles)}");
```

</TabItem>
<TabItem label="DI あり">

```csharp
var result = JsonSchemaGenerator.Write<AppSettings, AppSettings.Fragment>(
    "schemas",
    AppJsonContext.Default);
Console.WriteLine($"書き出し: {string.Join(", ", result.WrittenFiles)}");
```

</TabItem>
</Tabs>

`Write` が作るのはローカルの出力ディレクトリ内のファイルです。そのディレクトリを GitHub Pages などへ公開する処理は別途行います。`https://example.com/schemas/` のような絶対 `schemaBaseUri` を渡すと、生成文書の `$id` は基底 URI と版付きファイル名を結合した値になり、生成される設定スキーマにはルートの任意 `$schema` プロパティが含まれます。出力先ディレクトリは変わりません。出力は版付きなので、モデルの `Version` を上げたら出し直します。対応する DataAnnotations はスキーマの制約に写ります。出力スキーマは Configlue の保存形式を表します。`$configlue` がモデル ID と版を持ち、`$value` が疎な設定フラグメントを持ちます。

## エディターと CI で使う

JSON ファイルソースの書き込み時に版つき参照を加えるには、`SchemaReferenceBaseUri` を指定します。モデルごとの版つきファイル名が基底 URI に追加されます。

```csharp
sources.FromJsonFile(new()
{
    Id = "settings",
    Path = "settings.json",
    SchemaReferenceBaseUri = "./schemas/"
});
```

保存される JSON のルートにスキーマ参照が加わります。設定本体は生成スキーマが表すエンベロープ内に保存されます。

```json
{
  "$schema": "./schemas/tutorial.settings.v1.json",
  "$configlue": { "id": "tutorial.settings", "version": 1 },
  "$value": {
    "Server": { "Host": "localhost", "Port": 8080 }
  }
}
```

YAML ファイルソースでは、`SchemaReferenceBaseUri` がファイル先頭に `yaml-language-server` のスキーマ指定コメントを加えます。`SectionPath` を使うソースにはルート参照を加えられません。参照先スキーマは別途公開してください。VS Code などのエディターでは補完とホバー説明が効き、CI ではスキーマ検証を噛ませられます。

次: [STEP 8: 版を上げて移行する](./08-migration.md)。モデルの変更と古いファイルの扱いを決めます。
