---
title: "STEP 9: NativeAOT に対応する"
description: ソース生成メタデータでトリミング安全に動かす。
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 9: NativeAOT に対応する

単一ファイル配布や起動速度のために NativeAOT を使う場合、リフレクション任せのシリアル化は動きません。Configlue はソース生成メタデータを渡す形で対応しています。

## コーデックにメタデータを渡す

JSON の場合、アプリ側の `JsonSerializerContext` をコーデックに渡します。YAML も生成シリアライザーのオプションとフラグメントスキーマを使う形です。

```csharp
using System.Text.Json.Serialization;
using Configlue.Provider.Json;

[JsonSerializable(typeof(AppSettings))]
internal partial class AppJsonContext : JsonSerializerContext;
```

生成オプションは DI の有無にかかわらず同じように登録できます。

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
conf.Add<AppSettings>(model => model.Sources(sources =>
{
    sources.JsonFile("settings.json")
        .SerializerOptions(AppJsonContext.Default.Options);
    sources.JsonFile("database.json")
        .Mount(settings => settings.Database)
        .SerializerOptions(AppJsonContext.Default.Options);
}));
```

</TabItem>
<TabItem label="DI あり">

```csharp
builder.Services.AddConfiglue(conf =>
{
    conf.Add<AppSettings>(model => model.Sources(sources =>
    {
        sources.JsonFile("settings.json")
            .SerializerOptions(AppJsonContext.Default.Options);
        sources.JsonFile("database.json")
            .Mount(settings => settings.Database)
            .SerializerOptions(AppJsonContext.Default.Options);
    }));
});
```

</TabItem>
</Tabs>

Configlue fragment は provider の生成 converter を使い、mounted subtree fragment も同様に扱います。context はモデルの通常 property 型を含め、生成 `Fragment` 型を参照する必要はありません。XML も同様に対応する生成メタデータを使います。

## 動く例で確かめる

リポジトリに NativeAOT のサンプルが同梱されています。

```sh
dotnet publish example/Example.ConsoleApp.NativeAot --configuration Release
```

発行して実行できれば、設定の読み書きがトリミング後も動いています。詳しい注意点は[NativeAOT](../advanced/native-aot.md)を見てください。

次: [STEP 10: YAML も受け付ける](./10-yaml.md)。YAML を主としつつ JSON も読める形にします。
