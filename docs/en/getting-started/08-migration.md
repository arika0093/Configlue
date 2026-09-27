---
title: "STEP 8: Version and migrate"
description: Raise the version for breaking changes and declare how old models migrate.
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 8: Version and migrate

Renames and reshapes always arrive eventually. In Configlue, breaking changes raise the `Version`, keep the old shape around, and declare the migration. Old files are converted, not discarded.

## Raise the version

Make the new shape Version 2 and keep the old one under a new name:

```csharp
// Version 2 (new shape)
[ConfiglueModel("tutorial.settings", Version = 2)]
public partial class AppSettings
{
    public string DisplayName { get; set; } = "ExampleApp";
}

// Version 1 (old shape)
[ConfiglueModel("tutorial.settings", Version = 1)]
public partial class AppSettingsV1
{
    public string Name { get; set; } = "ExampleApp";
}
```

## Write the migration

```csharp
[ConfigluePreviousVersion(typeof(AppSettingsV1))]
public partial class AppSettings
{
    public AppSettings Migrate(AppSettingsV1 source) => new()
    {
        DisplayName = source.Name,
    };
}
```

Same-name, type-compatible members copy via `Fragment.FromPrevious`, so only renamed members need explicit code. For per-source chains, register `IStateSchemaMigration<T>` implementations as services.

## Moving storage follows the same idea

Separately from shape migration, storage moves (splitting files, changing formats, retiring old sources) travel through verified copies.

After registering both source IDs as described in the storage guide, call the same migration from either app style:

<Tabs syncKey="di">
<TabItem label="Without DI">

```csharp
var options = context.GetOptions<AppSettings>();
var result = await options.MigrateSourceAsync("legacy-settings", "settings");
Console.WriteLine($"Copied from {result.SourceId} to {result.TargetId}.");
```

</TabItem>
<TabItem label="With DI">

```csharp
public sealed class SettingsMigrator(IWritableOptions<AppSettings> options)
{
    public async Task MigrateAsync()
    {
        var result = await options.MigrateSourceAsync("legacy-settings", "settings");
        Console.WriteLine($"Copied from {result.SourceId} to {result.TargetId}.");
    }
}
```

</TabItem>
</Tabs>

The verify-then-retire flow, bulk multi-target moves, and post-migration source retirement live in [storage migration](../migration/storage-migration.md).

Next: [STEP 9: Support NativeAOT](./09-native-aot.md). Make the setup trimming-safe.
