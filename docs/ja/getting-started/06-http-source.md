---
title: "STEP 6: HTTP ソースに対応する"
description: 設定の一部だけをリモートから受け取り、読み取り専用として重ねる。
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 6: HTTP ソースに対応する

会社で配るポリシーや、サーバー側で一元管理したい項目は、HTTP で受け取る形が向いています。ここでは設定クラスの一部分だけを HTTP ソースから供給し、読み取り専用として重ねます。

## モデルの一部分をリモートにする

たとえば `Policy` だけを会社配布にします。ファイル側には持たせず、HTTP 層が来たときだけ値が付きます。

```csharp
[ConfiglueModel("tutorial.settings", Version = 1)]
public partial class AppSettings
{
    public string Name { get; set; } = "ExampleApp";
    public ServerSettings Server { get; set; } = new();
    public PolicySettings? Policy { get; set; }
}

public class PolicySettings
{
    public bool AuditLogRequired { get; set; }
    public int MaxLoginAttempts { get; set; } = 5;
}
```

## HTTP ソースを足す

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
using Configlue.Provider.Json;

var httpClient = new HttpClient { BaseAddress = new Uri("https://policy.example.com/") };

conf.Add<AppSettings>(model =>
{
    model.Sources(sources =>
    {
        sources.FromJsonFile(new() { Id = "settings", Path = "settings.json", Priority = 100 });
        sources.FromJsonHttp(new()
        {
            Id = "policy",
            EndPoint = "https://policy.example.com/",
            Client = httpClient,
            Priority = 300,
            FallbackCondition = StateFallbackCondition.NotFoundOrUnavailable,
        });
    });
    model.WriteRoute = StateWriteRoute.To("settings");
});
```

`HttpClient` はアプリ所有のままです。Configlue は破棄しません。

</TabItem>
<TabItem label="DI あり">

```csharp
// 標準の AddHttpClient で名前付きクライアントを登録します。
builder.Services.AddHttpClient("policy", client =>
{
    client.BaseAddress = new Uri("https://policy.example.com/");
});

builder.Services.AddConfiglue(conf =>
{
    conf.Add<AppSettings>(model =>
    {
        model.Sources(sources =>
        {
            sources.FromJsonFile(new() { Id = "settings", Path = "settings.json", Priority = 100 });
            sources.FromJsonHttpClientFactory("policy", new()
            {
                Id = "policy",
                EndPoint = "https://policy.example.com/",
                Priority = 300,
                FallbackCondition = StateFallbackCondition.NotFoundOrUnavailable,
            });
        });
        model.WriteRoute = StateWriteRoute.To("settings");
    });
});
```

`IHttpClientFactory` がハンドラーを管理し、ソースは返されたクライアントを破棄しません。

</TabItem>
</Tabs>

HTTP ソースは既定で読み取り専用です。相手が更新を受け付けるときだけ `Writable = true` にし、明示的にコーデックを渡します。取得は ETag による条件付き書き込みとポーリングに対応しています。

## 取れないときはどうなるか

ポリシーがない・一時的に取れない場合はファイル層にフォールスルーし、アプリは手元の値で動きます。恒久的な HTTP エラーはアプリに伝わります。動く形は `example/Example.MultiSource` が参考になります。

```sh
dotnet run --project example/Example.MultiSource
```

`CONFIGLUE_POLICY_URL` にルートを設定するとリモート層が有効になります。

保存の考え方は STEP 5 と同じです。HTTP 層が握っている項目をファイル側から変えようとすると競合になります。`ExplainAsync("Policy.AuditLogRequired")` で出どころを確かめてください。

次: [STEP 7: JSON Schema を出力する](./07-json-schema.md)。エディター補完と CI の検査に使います。
