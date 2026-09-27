---
title: "STEP 3: 共通とローカルに分ける"
description: StandardPath・優先度・読みの規則・書き込み先の暗黙と明示。
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 3: 共通とローカルに分ける

ひとつのファイルに全部詰めると、出荷既定とユーザー設定が混ざります。ここではファイルを「共通（グローバル）」と「ローカル」に分けます。読みは重ね合わせ、書きは宛先指定。この二文が体に入れば、Configlue の半分は掴んだも同然です。

## 優先度の約束

複数ソースに同じ項目があるとき、`Priority` の数字が大きい方が勝ちます。数字自体に意味はなく、順番だけが大事です。

| ソース | 役割 | 優先度（例） |
| --- | --- | ---: |
| `common.global` | 出荷既定・全ユーザー共通 | 100 |
| `common.local` | このマシンの上書き | 200 |

ファイルがない場合、ファイルソースは素通りします。ある項目だけが合成され、ない項目は下の層の値が残ります。

## 定番の分け方（プリセットを使う）

手で二層を組むこともできますが、定番形にはプリセットがあります。`Configlue.Source.Common` パッケージの `UseCommonSources` が、共通・ローカル・指定ファイル・環境変数・コマンドラインを束ねます。

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
using Configlue.Source.Common;

await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<AppSettings>(model => model.UseCommonSources(new CommonSourceOptions
    {
        ApplicationId = "ExampleApp",
        GlobalFileName = "settings.json",
        WriteLayer = CommonSourceWriteLayer.Local,
    }));
});
```

</TabItem>
<TabItem label="DI あり">

```csharp
using Configlue.Source.Common;

builder.Services.AddConfiglue(conf =>
{
    conf.Add<AppSettings>(model => model.UseCommonSources(new CommonSourceOptions
    {
        ApplicationId = "ExampleApp",
        GlobalFileName = "settings.json",
        WriteLayer = CommonSourceWriteLayer.Local,
    }));
});
```

</TabItem>
</Tabs>

`ApplicationId` からプラットフォーム標準の保存ディレクトリが決まります（`ConfiglueStandardPaths.GetStandardSaveDirectory`）。共通ファイルはそこに、ローカルファイルは実行ディレクトリに置かれます。`WriteLayer` が「保存先はローカル」の宣言です。登録時に選べる宛先はひとつだけで、無効な層を選ぶと例外になります。

## 手で二層を組む

仕組みを掴むには、手書きも一度見ておくと早いです。

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
using Configlue.Provider.Json;

conf.Add<AppSettings>(model =>
{
    model.Sources(sources =>
    {
        sources.FromJsonFile(new() { Id = "global", Path = globalPath, Priority = 100 });
        sources.FromJsonFile(new() { Id = "local", Path = "settings.local.json", Priority = 200 });
    });
    model.WriteRoute = StateWriteRoute.To("local");
});
```

</TabItem>
<TabItem label="DI あり">

```csharp
using Configlue.Provider.Json;

builder.Services.AddConfiglue(conf =>
{
    conf.Add<AppSettings>(model =>
    {
        model.Sources(sources =>
        {
            sources.FromJsonFile(new() { Id = "global", Path = globalPath, Priority = 100 });
            sources.FromJsonFile(new() { Id = "local", Path = "settings.local.json", Priority = 200 });
        });
        model.WriteRoute = StateWriteRoute.To("local");
    });
});
```

</TabItem>
</Tabs>

読みは両方から合成されます。`local` にある項目は `global` に勝ち、`local` にない項目は `global` の値が残ります。書き込みは `WriteRoute` の指す `local` にだけ届き、`global` は汚しません。

## 読みの規則・書きの規則

- **読み:** 存在する項目だけを優先度順に合成する。ファイルがなくても黙って素通りし、それ以外の読み取り失敗は例外として伝える。
- **書き:** 既定では `WriteRoute` の指すひとつのソースにだけ書く。項目ごとに宛先を分けたいときは `WritePlan` を使う（[書き込み経路指定](../layering/write-routing.md)）。
- **宛先の決め方:** `WriteLayer` や `WriteRoute` で暗黙に決めておき、操作ごとに変えたいときだけ明示的に指定する。どこに書くかは常にコードから読める状態にしておきます。

`ExplainAsync("Server.Port")` で実効値と寄与を並べると、重ね合わせが目に見えます。ぜひ試してください。

次: [STEP 4: バリデーションを付ける](./04-validation.md)。間違った値を保存前に止めます。
