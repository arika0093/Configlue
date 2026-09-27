---
title: "STEP 3: Split shared and local"
description: Standard paths, priority, read rules, and implicit vs explicit write destinations.
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 3: Split shared and local

One file for everything mixes shipped defaults with user settings. Here we split files into "shared (global)" and "local" layers. Reads layer; writes target. Once those two sentences feel natural, you know half of Configlue.

## The priority promise

When the same field exists in several sources, the larger `Priority` number wins. The numbers mean nothing except order.

| Source | Role | Priority (example) |
| --- | --- | ---: |
| `common.global` | Shipped defaults, shared by all users | 100 |
| `common.local` | Overrides on this machine | 200 |

A missing file source just falls through. Present fields compose; fields missing above leave the lower layer's value intact.

## The standard split (using the preset)

You can build two layers by hand, but the standard shape has a preset. `UseCommonSources` from the `Configlue.Source.Common` package bundles shared, local, selected-file, and environment layers. Add command-line overrides separately from `Configlue.Source.CommandLine`.

<Tabs syncKey="di">
<TabItem label="Without DI">

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
<TabItem label="With DI">

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

`ApplicationId` decides the platform-standard save directory (`ConfiglueStandardPaths.GetStandardSaveDirectory`). The shared file lives there, the local file next to the executable. `WriteLayer` declares "saves go to local". Exactly one destination is chosen at registration; picking a disabled layer throws.

## Building two layers by hand

Seeing the manual form once makes the mechanism click:

<Tabs syncKey="di">
<TabItem label="Without DI">

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
<TabItem label="With DI">

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

Reads compose from both. Fields present in `local` beat `global`; fields missing in `local` keep `global`'s value. Writes reach only `local` via `WriteRoute`; `global` stays clean.

## Read rules, write rules

- **Reads:** compose only present fields by priority. Missing files fall through silently; other read failures surface as exceptions.
- **Writes:** go to the single `WriteRoute` source by default. To split destinations per path, use a `WritePlan` ([write routing](../layering/write-routing.md)).
- **Choosing destinations:** decide implicitly with `WriteLayer`/`WriteRoute`, and specify explicitly only for the operation that differs. Where a save lands should always be readable from code.

Run `(await options.GetDetailsAsync()).Server.Port` to see the effective value and contributions side by side.

Next: [STEP 4: Add validation](./04-validation.md). Reject bad values before they are saved.
