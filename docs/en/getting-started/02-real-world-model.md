---
title: "STEP 2: Grow a realistic model"
description: Add nesting and collections until the model looks like a real settings file.
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 2: Grow a realistic model

`Name` plus `RunCount` smells nothing like the real world. Here we add server, database, and logging nests until the file looks plausible. Only the model grows — the read/write shape stays the same.

## Expand the model

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

Nested classes need no attribute. Only the root — the unit of configuration — carries `[ConfiglueModel]`. Defaults mean "the value when no source says anything". Unset and default stay distinguished, so splitting files later won't break.

## Registration stays the same

<Tabs syncKey="di">
<TabItem label="Without DI">

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
<TabItem label="With DI">

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

Growing the model changes nothing about registration. Source definitions only say where to read from, never how the model looks.

## Deep edits still read like plain C#

```csharp
await options.SaveAsync(settings =>
{
    settings.Server.Port = 9000;
    settings.Database.Host = "db.internal";
    settings.EnabledFeatures.Add("audit-log");
});
```

Nested assignment and collection adds are just C#. Underneath, only changed fields reach the `settings` source; untouched ones stay put.

The saved `settings.json` looks like this:

```json
{
  "$configlue": { "id": "tutorial.settings", "version": 1 },
  "$value": {
    "Name": "ExampleApp",
    "Server": { "Host": "localhost", "Port": 9000 },
    "Database": { "Host": "db.internal", "Port": 5432 },
    "EnabledFeatures": ["audit-log"]
  }
}
```

## Ask where a value came from

More fields means more "where did this come from?". Trace it with `ExplainAsync`:

```csharp
var explanation = await options.ExplainAsync("Database.Host");
Console.WriteLine(explanation);
```

It lists the effective value and each source contribution from highest to lowest priority. Trivial with one source — powerful once STEP 3 splits them.

Next: [STEP 3: Split shared and local](./03-global-local.md). Divide files and learn priority and write destinations.
