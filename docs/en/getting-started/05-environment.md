---
title: "STEP 5: Support environment variables"
description: Layer a read-only source and learn how saves behave around it.
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 5: Support environment variables

Containers and CI want overrides through environment variables. Environment sources are **read-only**. They layer like files, but saves behave differently. Grasp this once and every read-only source makes sense.

## Layer environment variables

Double underscores separate nesting. With prefix `EXAMPLE`, `EXAMPLE__SERVER__PORT` maps to `Server.Port`. Member names match case-insensitively.

<Tabs syncKey="di">
<TabItem label="Without DI">

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
<TabItem label="With DI">

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

Priority-400 environment variables beat the priority-100 file. Start with `EXAMPLE__SERVER__PORT=9000` and that value takes effect.

## Saving while a read-only source shadows a value

Environment variables cannot be written. So what happens when `Server.Port` is overridden by one and you call `SaveAsync` on it?

It fails — with a `StateConflictException`. A read-only contribution shadows the requested value, and silently ignoring the environment to save would be scarier.

```csharp
// Fails with a conflict while EXAMPLE__SERVER__PORT is set.
await options.SaveAsync(patch => patch.Server!.Port = 9000);
```

Pick one of these:

- Remove the environment variable, then save (the runtime environment wins).
- Save only fields the environment does not override.
- For permanent overrides, treat the environment as truth and stop keeping that field in files.

`(await options.GetDetailsAsync()).Server.Port` shows which layer holds the value. Checking before saving makes conflict reasons obvious.

## Swappable for tests

Process environment is awkward in tests, so the facade supports `EnvironmentVariables` overrides and a `ValueParser`. See [environment and command line](../sources/environment-and-commandline.md).

Next: [STEP 6: Add an HTTP source](./06-http-source.md). Take part of the settings from a remote endpoint.
