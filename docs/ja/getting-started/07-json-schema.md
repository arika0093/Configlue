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

`Write` が作るのはローカルの出力ディレクトリ内のファイルです。そのディレクトリを GitHub Pages などへ公開する処理は別途行います。`https://example.com/schemas/` のような絶対 `schemaBaseUri` を渡すと、生成文書の `$id` は基底 URI と版付きファイル名を結合した値になり、設定データ内の任意の `$schema` 項目も許可します。出力先ディレクトリは変わりません。出力は版付きなので、モデルの `Version` を上げたら出し直します。対応する DataAnnotations はスキーマの制約に写ります。

## エディターと CI で使う

JSON ファイルの先頭で `$schema` を指すのが手軽です。

```json
{
  "$schema": "./schemas/appsettings.schema.json",
  "$version": 1,
  "Server": { "Host": "localhost", "Port": 8080 }
}
```

VS Code などのエディターでは補完とホバー説明が効き、CI ではスキーマ検証を噛ませられます。手書き JSON のtypoに実行時まで気づかない、という事故が減ります。

次: [STEP 8: 版を上げて移行する](./08-migration.md)。モデルの変更と古いファイルの扱いを決めます。
