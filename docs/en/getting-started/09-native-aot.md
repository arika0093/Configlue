---
title: "STEP 9: Support NativeAOT"
description: Run trimming-safe with source-generated metadata.
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 9: Support NativeAOT

For single-file deployment or fast startup, reflection-based serialization won't survive NativeAOT. Configlue accepts source-generated metadata instead.

## Pass metadata to the codec

For JSON, hand your `JsonSerializerContext` to the codec. YAML uses generated serializer options with the fragment schema the same way.

```csharp
using System.Text.Json.Serialization;
using Configlue.Provider.Json;

[JsonSerializable(typeof(AppSettings))]
internal partial class AppJsonContext : JsonSerializerContext;
```

Register the same generated options with or without DI:

<Tabs syncKey="di">
<TabItem label="Without DI">

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
<TabItem label="With DI">

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

The provider uses generated converters for Configlue fragments, including mounted subtree fragments. The context covers the model's ordinary property types; it does not need to reference generated `Fragment` types. XML follows the same pattern with its generated metadata.

## Verify with the sample

The repo ships a NativeAOT sample:

```sh
dotnet publish example/Example.ConsoleApp.NativeAot --configuration Release
```

If it publishes and runs, settings read/write survived trimming. Details live in [NativeAOT](../advanced/native-aot.md).

Next: [STEP 10: Accept YAML too](./10-yaml.md). Lead with YAML while still reading JSON.
