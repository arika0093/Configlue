---
title: "STEP 5: 環境変数に対応する"
description: 読み取り専用ソースの重ね方と、保存時の振る舞い。
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 5: 環境変数に対応する

コンテナや CI では、環境変数で上書きしたい場面が出てきます。環境変数ソースは**読み取り専用**です。重ね方はファイルと同じですが、保存時の振る舞いが違います。ここを押さえると、読み取り専用全般の扱いが分かります。

## 環境変数を重ねる

`__`（アンダースコア二つ）で入れ子を区切ります。接頭辞 `EXAMPLE` なら `EXAMPLE__SERVER__PORT` が `Server.Port` になります。メンバー名の大文字小文字は問いません。

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
using Configlue.Source.Environment;

conf.Add<AppSettings>(model =>
{
    model.Sources(sources =>
    {
        sources.FromJsonFile(new() { Id = "settings", Path = "settings.json", Priority = 100 });
        sources.Add(EnvironmentStateSource.FromEnvironment<AppSettings, AppSettings.Fragment>(
            "environment", "EXAMPLE", priority: 400));
    });
    model.WriteRoute = StateWriteRoute.To("settings");
});
```

</TabItem>
<TabItem label="DI あり">

```csharp
using Configlue.Provider.Json;
using Configlue.Source.Environment;

builder.Services.AddConfiglue(conf =>
{
    conf.Add<AppSettings>(model =>
    {
        model.Sources(sources =>
        {
            sources.FromJsonFile(new() { Id = "settings", Path = "settings.json", Priority = 100 });
            sources.FromEnvironment(new() { Id = "environment", Prefix = "EXAMPLE", Priority = 400 });
        });
        model.WriteRoute = StateWriteRoute.To("settings");
    });
});
```

</TabItem>
</Tabs>

優先度 400 の環境変数が、ファイル（100）に勝ちます。`EXAMPLE__SERVER__PORT=9000` を設定して起動すれば、その値が有効になります。

## 読み取り専用があるときの保存

環境変数は書き込めません。では `Server.Port` が環境変数で上書きされているときに `SaveAsync` したらどうなるでしょう。

答えは「失敗します」。読み取り専用の寄与が隠している値を、書き込み可能なソース側から変えようとすると、`StateConflictException` になります。黙って環境変数を無視して保存する方が怖いからです。

```csharp
// EXAMPLE__SERVER__PORT が設定されていると、この保存は競合で失敗します。
await options.SaveAsync(patch => patch.Server!.Port = 9000);
```

対処はどれかです。

- 環境変数を外してから保存する（実行環境の指定を優先する）。
- 環境変数で上書きされていない項目だけを保存する。
- 恒久的な上書き運用なら、環境変数を正としてファイル側を持たない。

`(await options.GetDetailsAsync()).Server.Port` を見れば、どの層が値を握っているか分かります。保存の前に確認する癖を付けると、競合の理由がすぐ読めます。

## テストでは差し替え可能に

プロセスの環境変数はテストしにくいので、ファサードの `EnvironmentVariables` 差し替えや `ValueParser` を使います。詳しくは[環境変数とコマンドライン](../sources/environment-and-commandline.md)を見てください。

次: [STEP 6: HTTP ソースに対応する](./06-http-source.md)。設定の一部だけをリモートから受け取ります。
