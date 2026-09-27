---
title: "STEP 6: Add an HTTP source"
description: Take part of the settings from a remote endpoint as a read-only layer.
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 6: Add an HTTP source

Company-distributed policy or centrally managed fields fit HTTP delivery. Here only part of the settings class comes from HTTP, layered as read-only.

## Make part of the model remote

For example, let only `Policy` be company-distributed. Files never hold it; values appear only when the HTTP layer arrives.

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

## Add the HTTP source

<Tabs syncKey="di">
<TabItem label="Without DI">

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

The `HttpClient` stays app-owned. Configlue never disposes it.

</TabItem>
<TabItem label="With DI">

```csharp
// Register a named client with the standard AddHttpClient APIs.
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

`IHttpClientFactory` manages handlers, and the source does not dispose the returned client.

</TabItem>
</Tabs>

HTTP sources are read-only by default. Set `Writable = true` with an explicit codec only when the endpoint accepts updates. Fetching supports ETags with conditional writes and polling.

## What happens when it is unreachable

If the policy is missing or temporarily unreachable, reads fall through to file layers and the app runs on local values. Permanent HTTP errors surface to the app. `example/Example.MultiSource` shows the working shape:

```sh
dotnet run --project example/Example.MultiSource
```

Set `CONFIGLUE_POLICY_URL` to enable the remote layer.

Saving follows STEP 5's logic: editing a field the HTTP layer holds from the file side causes a conflict. Check origins with `(await options.GetDetailsAsync()).Policy.AuditLogRequired`.

Next: [STEP 7: Export JSON Schema](./07-json-schema.md). Feed editor completion and CI checks.
