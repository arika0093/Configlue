---
title: Named instances and dynamic options
description: OptionsName-based named instances with runtime add and remove.
---

A model can opt in to dynamic named options with `model.EnableDynamicOptions = true`. The context exposes `GetOptionsRegistry<TModel>()`; `TryAdd(name)` creates the same source/model configuration under that `OptionsName`, and `TryRemoveAsync(name)` stops its watcher and disposes helper-created resources before returning.

```csharp
config.Add<AppSettings>(model =>
{
    model.EnableDynamicOptions = true;
    model.Sources(sources => sources.FromJsonFile(new()
    {
        Id = "tenant-settings",
        Path = "settings.json",
    }));
});

var registry = context.GetOptionsRegistry<AppSettings>();
registry.TryAdd("tenant-a");
var tenantOptions = context.GetOptions<AppSettings>("tenant-a");
await registry.TryRemoveAsync("tenant-a");
```

In DI, `IOptionsMonitor<AppSettings>.Get("tenant-a")` follows additions and removals through the registry; `Get` throws after removal. Resolve dynamic writable options through `IConfiglueInspectionRegistry<AppSettings>.Get(name)`; keyed services are fixed when the provider is built and are not created for later names. An already materialized `IOptionsSnapshot<T>` keeps its value for that scope, as snapshots normally do.

Dynamic named options are runtime-only; persisted profile catalogs are available separately through `EnableProfiles`. For runtime profiles created during execution, register `AddConfiglueOptionsRegistry<TModel, TModel.Fragment>(...)`, then use `IConfiglueInspectionRegistry<TModel>.TryAdd`, `Get`, and `TryRemove`.

Fixed registration names are reserved. Removal prevents new operations from starting on that runtime, waits for operations already in progress and the watcher to stop, then disposes helper-created resources. New context lookups fail after removal. Previously returned handles become disposed; a configure session saved after its options were removed fails with `ObjectDisposedException`.

The source set is fixed for each options runtime. A dynamic named-options addition creates a separate runtime from its registration definition, and removal retires that whole runtime; neither operation changes another runtime's source topology.

## Next steps

* [Profiles](./profiles.md) for persisted catalogs.
* [Application setup](../basic-usage/app-setup.md) for custom source lifecycles.
