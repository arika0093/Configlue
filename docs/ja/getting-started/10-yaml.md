---
title: "STEP 10: YAML も受け付ける"
description: YAMLを主としつつJSONも読み続ける。形式を混ぜる仕上げ。
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 10: YAML も受け付ける

最後は形式の混ぜ方です。メインを YAML にしつつ、既存の JSON も読み続けます。形式が違っても、ソースの考え方は同じです。パッケージの追加だけ忘れずに。

```bash
dotnet add package Configlue.Provider.Yaml
```

## YAML を主・JSON を従にする

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
using Configlue.Provider.Yaml;
using Configlue.Provider.Json;

conf.Add<AppSettings>(model =>
{
    model.Sources(sources =>
    {
        // 主：YAML（書き込み先）
        sources.FromYamlFile(new()
        {
            Id = "settings-yaml",
            Path = "settings.yaml",
            Priority = 200,
        });
        // 従：既存の JSON（読みだけ、なければ素通り）
        sources.FromJsonFile(new()
        {
            Id = "settings-json",
            Path = "settings.json",
            Priority = 100,
        });
    });
    model.WriteRoute = StateWriteRoute.To("settings-yaml");
});
```

</TabItem>
<TabItem label="DI あり">

```csharp
using Configlue.Provider.Yaml;
using Configlue.Provider.Json;

builder.Services.AddConfiglue(conf =>
{
    conf.Add<AppSettings>(model =>
    {
        model.Sources(sources =>
        {
            sources.FromYamlFile(new()
            {
                Id = "settings-yaml",
                Path = "settings.yaml",
                Priority = 200,
            });
            sources.FromJsonFile(new()
            {
                Id = "settings-json",
                Path = "settings.json",
                Priority = 100,
            });
        });
        model.WriteRoute = StateWriteRoute.To("settings-yaml");
    });
});
```

</TabItem>
</Tabs>

読みは両形式を合成します。YAML にある項目は YAML が勝ち、ない項目は JSON の値が残ります。書き込みは `settings-yaml` にだけ届くので、JSON 側は読み専用の資産として残せます。いずれ JSON を捨てる場合は、[保存場所の移行](../migration/storage-migration.md)で検証付きコピーしてから退役させます。

動く例は `example/Example.ConsoleApp.Yaml` です。

```sh
dotnet run --project example/Example.ConsoleApp.Yaml
```

## チュートリアル完走のあとは

10 ステップで、読み書き・分割・検証・環境変数・HTTP・スキーマ・移行・NativeAOT・YAML を一通り触りました。ここから先は辞書的に使ってください。

- 機能の詳しい使い方は「機能説明」。やりたいことからは [機能説明について](../guides/overview.md) が近道です。
- 仕組みの理解には「設計」（Resource / Source / Codec / Fragment・Patch / Options と全体像）。
- 手元で試せる完成品は[サンプル集](./examples.md)。

おつかれさまでした。設定の糊づけ、うまくいきますように。
