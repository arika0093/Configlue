---
title: "STEP 2: 現実的なモデルに育てる"
description: 入れ子とコレクションを増やし、実世界の設定ファイルらしい形にする。
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 2: 現実的なモデルに育てる

STEP 1 の `Name` と `RunCount` だけでは、実世界の匂いがしません。ここではサーバー・データベース・ログといった入れ子を足し、「ありそうな設定ファイル」に育てます。やることはモデルの拡張だけで、読み書きの形は変わりません。

## モデルを拡張する

```csharp
using Configlue;

[ConfiglueModel("tutorial.settings", Version = 1)]
public partial class AppSettings
{
    public string Name { get; set; } = "ExampleApp";
    public ServerSettings Server { get; set; } = new();
    public DatabaseSettings Database { get; set; } = new();
    public List<string> EnabledFeatures { get; set; } = [];
}

public class ServerSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 8080;
}

public class DatabaseSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string? Password { get; set; }
}
```

入れ子のクラスには属性は要りません。設定単位の起点となるルートだけが `[ConfiglueModel]` を持ちます。既定値は「ソースが何も言わなかったときの値」です。未設定と既定値は区別されるので、後でファイルを分割しても壊れません。

## 登録はそのまま

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<AppSettings>(model =>
    {
        model.Sources(sources => sources.FromJsonFile(new()
        {
            Id = "settings",
            Path = "settings.json",
        }));
        model.WriteRoute = StateWriteRoute.To("settings");
    });
});
```

</TabItem>
<TabItem label="DI あり">

```csharp
builder.Services.AddConfiglue(conf =>
{
    conf.Add<AppSettings>(model =>
    {
        model.Sources(sources => sources.FromJsonFile(new()
        {
            Id = "settings",
            Path = "settings.json",
        }));
        model.WriteRoute = StateWriteRoute.To("settings");
    });
});
```

</TabItem>
</Tabs>

モデルを育てても登録は同じです。ソース定義は「どこから読むか」だけを言い、モデルの形には立ち入りません。

## 深い編集も普段どおりに書く

```csharp
await options.SaveAsync(settings =>
{
    settings.Server.Port = 9000;
    settings.Database.Host = "db.internal";
    settings.EnabledFeatures.Add("audit-log");
});
```

入れ子の代入もコレクションの追加も、普段の C# です。裏側では変更のあった項目だけが `settings` ソースに届き、触っていない項目はそのまま残ります。

保存後の `settings.json` はこんな形です。

```json
{
  "$version": 1,
  "Name": "ExampleApp",
  "Server": { "Host": "localhost", "Port": 9000 },
  "Database": { "Host": "db.internal", "Port": 5432 },
  "EnabledFeatures": ["audit-log"]
}
```

## 値の出どころを確かめる

項目が増えると「この値、どこから来た？」が気になります。`ExplainAsync` で辿れます。

```csharp
var explanation = await options.ExplainAsync("Database.Host");
Console.WriteLine(explanation);
```

実効値と、優先度順の各ソースの寄与が並びます。今はソースがひとつなので答えは単純ですが、STEP 3 で分割すると威力を発揮します。

次: [STEP 3: 共通とローカルに分ける](./03-global-local.md)。ファイルを分割し、優先度と書き込み先の考え方を掴みます。
