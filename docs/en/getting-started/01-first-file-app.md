---
title: "STEP 1: Your first file-based app"
description: Read and write a JSON file with change notifications. The smallest possible app.
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 1: Your first file-based app

This tutorial runs in order. STEP 1 covers only the smallest shape: reading and writing a single JSON file, plus external-change notifications. Switch the tabs for with/without DI — your choice carries over to later STEPs.

## Declare a model

The settings container is an ordinary C# class. The only rules: add `[ConfiglueModel]` and mark it `partial`. The generator creates `Fragment`/`Patch` support for diffing.

```csharp
using Configlue;

[ConfiglueModel("tutorial.settings", Version = 1)]
public partial class AppSettings
{
    public string Name { get; set; } = "World";
    public int RunCount { get; set; }
}
```

The first string is a schema ID. Give it a unique dotted name inside your app — it becomes the anchor for JSON Schema export and versioning later.

## Create a context

<Tabs syncKey="di">
<TabItem label="Without DI">

```csharp
using Configlue;
using Configlue.Provider.Json;

await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<AppSettings>(model =>
    {
        model.UseDefaultJsonFile();
    });
});

var options = context.GetOptions<AppSettings>();
```

</TabItem>
<TabItem label="With DI">

```csharp
// Program.cs
using Configlue;
using Configlue.Provider.Json;

builder.Services.AddConfiglue(conf =>
{
    conf.Add<AppSettings>(model =>
    {
        model.UseDefaultJsonFile();
    });
});
```

```csharp
// Consumer
using Configlue;

public class Greeter(IWritableOptions<AppSettings> options)
{
    public async Task RunAsync()
    {
        var settings = await options.GetValueAsync();
        Console.WriteLine($"Hello, {settings.Name}!");
    }
}
```

</TabItem>
</Tabs>

`UseDefaultJsonFile()` registers a writable `settings` source at `usersettings.json` beside the application executable. Use `UseJsonFile("settings.json")` to choose a path relative to the current directory, or pass a complete `JsonFileSourceOptions` object for full control. Layered configurations can use `Sources(...FromJsonFile...)` and `WriteRoute` directly.

## Read, watch, save

<Tabs syncKey="di">
<TabItem label="Without DI">

```csharp
var current = await options.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}! (run #{current.RunCount})");

// Editing the file externally triggers this.
using var subscription = options.OnChange(updated =>
    Console.WriteLine($"Changed: {updated.Name}"));

await options.SaveAsync(patch =>
{
    patch.Name = "Ada";
    patch.RunCount = current.RunCount + 1;
});
```

</TabItem>
<TabItem label="With DI">

```csharp
var current = await options.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}! (run #{current.RunCount})");

using var subscription = options.OnChange(updated =>
    Console.WriteLine($"Changed: {updated.Name}"));

await options.SaveAsync(patch =>
{
    patch.Name = "Ada";
    patch.RunCount = current.RunCount + 1;
});
```

The read/write code is identical with or without DI. Only context creation differs.

</TabItem>
</Tabs>

The generated Patch passed to `SaveAsync` is sparse: only specified members are saved, and untouched ones keep their source values. Afterwards `settings.json` holds just this source's members:

```json
{
  "$version": 1,
  "Name": "Ada",
  "RunCount": 1
}
```

If the file is missing on first run, you start from model defaults — file sources fall through instead of throwing. Editing the file by hand while running fires `OnChange` (debounced at about 300ms).

## Stumbling points

- Forgetting `partial` stops the generator. The build error tells you.
- Registration must come before `GetOptions`. Remember "register, then resolve".
- Lost about where saves go? Re-check `WriteRoute` — the destination is always explicit.

Next: [STEP 2: Grow a realistic model](./02-real-world-model.md). Add fields and nesting until it looks like a real-world settings file.
