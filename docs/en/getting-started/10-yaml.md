---
title: "STEP 10: Accept YAML too"
description: Lead with YAML while still reading JSON. Mixing formats, the finale.
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 10: Accept YAML too

The finale is mixing formats: lead with YAML while still reading existing JSON. Formats differ, but sources work the same. Just remember the extra package:

```bash
dotnet add package Configlue.Provider.Yaml
```

## YAML first, JSON second

<Tabs syncKey="di">
<TabItem label="Without DI">

```csharp
using Configlue.Provider.Yaml;
using Configlue.Provider.Json;

conf.Add<AppSettings>(model =>
{
    model.Sources(sources =>
    {
        // Primary: YAML (the write destination)
        sources.FromYamlFile(new()
        {
            Id = "settings-yaml",
            Path = "settings.yaml",
            Priority = 200,
        });
        // Secondary: existing JSON (read-only in practice, falls through if missing)
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
<TabItem label="With DI">

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

Reads compose both formats. Fields present in YAML win; missing ones keep JSON values. Writes reach only `settings-yaml`, so JSON stays as a read-only asset. To drop JSON later, copy with verification and retire it ([storage migration](../migration/storage-migration.md)).

The runnable sample is `example/Example.ConsoleApp.Yaml`:

```sh
dotnet run --project example/Example.ConsoleApp.Yaml
```

## After the tutorial

Ten STEPs covered read/write, splitting, validation, environment, HTTP, schema, migration, NativeAOT, and YAML. From here, use the docs as a dictionary:

- "Guides" for how to use each feature (reading/writing, app setup, common sources, changes and validation, sources, layering, profiles, migration, advanced topics).
- "Design" for how it works (Resource / Source / Codec / Fragment and Patch / Options, plus the overview).
- [Examples](./examples.md) for runnable finished pieces.

Thanks for sticking through. Happy gluing.
