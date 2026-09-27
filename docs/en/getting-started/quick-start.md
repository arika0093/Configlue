---
title: Quick start
description: Read and write layered settings in five minutes, without DI.
---

# Quick start

This guide reads settings from a JSON file, layers process environment variables on top, and saves an edit — all without a DI container.

## 1. Declare the model

```csharp
using Configlue;

[ConfiglueModel("example.quick-settings", Version = 1)]
public partial class QuickSettings
{
    public string Name { get; set; } = "World";
    public int RunCount { get; set; }
}
```

## 2. Create a context with two sources

Higher `Priority` wins for members present in more than one source, so environment variables override the file here. File sources fall through when the file is missing — the first run starts from model defaults.

```csharp
using Configlue;
using Configlue.Provider.Json;
using Configlue.Source.Environment;

await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<QuickSettings>(model =>
    {
        model.Sources(sources =>
        {
            sources.FromJsonFile(new()
            {
                Id = "settings",
                Path = "quicksettings.json",
                Priority = 100,
            });
            sources.Add(EnvironmentStateSource.FromEnvironment<QuickSettings, QuickSettings.Fragment>(
                "environment", "QUICK"));
        });
        model.WriteRoute = StateWriteRoute.To("settings");
    });
});

var options = context.GetOptions<QuickSettings>();
```

With the prefix `QUICK`, the variable `QUICK__NAME` overrides `Name`. Member names match case-insensitively; `__` separates nested members.

## 3. Read, watch, and save

```csharp
var current = await options.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}! (run #{current.RunCount})");

using var subscription = options.OnChange(updated =>
    Console.WriteLine($"Settings changed: {updated.Name}"));

// Sparse patch: only specified members are written.
await options.SaveAsync(patch =>
{
    patch.Name = "Ada";
    patch.RunCount = current.RunCount + 1;
});
```

Open `quicksettings.json` afterwards — it contains only the members this source owns:

```json
{
  "$version": 1,
  "Name": "Ada",
  "RunCount": 1
}
```

## 4. See where a value came from

```csharp
var details = await options.GetDetailsAsync();
Console.WriteLine(details.Name);
```

The details tree lists the effective value and each source contribution from highest to lowest priority, with editability attached.

## Next steps

* Using DI? The same `conf.Add<T>(...)` definition goes inside `services.AddConfiglue(...)` — see [Application setup](../basic-usage/app-setup.md).
* Tired of wiring common layers by hand? See [Common layered sources](../basic-usage/common-sources.md).
* Run the full samples in [Examples](./examples.md).
