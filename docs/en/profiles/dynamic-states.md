---
title: Named instances and dynamic states
description: StateName-based named instances with runtime add and remove.
---

A model can opt in to dynamic named states with `model.EnableDynamicStates = true`. The context exposes `GetStateRegistry<TModel>()`; `TryAdd(name)` creates the same source/model configuration under that `StateName`, and `TryRemoveAsync(name)` stops its watcher and disposes helper-created resources before returning.

```csharp
config.Add<AppSettings>(model =>
{
    model.EnableDynamicStates = true;
    model.Sources(sources => sources.FromJsonFile(new()
    {
        Id = "tenant-settings",
        Path = "settings.json",
    }));
});

var registry = context.GetStateRegistry<AppSettings>();
registry.TryAdd("tenant-a");
var tenantState = context.GetState<AppSettings>("tenant-a");
await registry.TryRemoveAsync("tenant-a");
```

In DI, `IOptionsMonitor<AppSettings>.Get("tenant-a")` follows additions and removals through the registry; `Get` throws after removal. Resolve dynamic writable states through `IConfiglueStateRegistry<AppSettings>.Get(name)`; keyed services are fixed when the provider is built and are not created for later names. An already materialized `IOptionsSnapshot<T>` keeps its value for that scope, as snapshots normally do.

Dynamic named states are runtime-only; persisted profile catalogs are available separately through `EnableProfiles`. For runtime states created during execution, register `AddConfiglueStateRegistry<TModel, TModel.Fragment>(...)`, then use `IConfiglueStateRegistry<TModel>.TryAdd`, `Get`, and `TryRemove`.

Fixed registration names are reserved. Removal prevents new operations from starting on that runtime, waits for operations already in progress and the watcher to stop, then disposes helper-created resources. New context lookups fail after removal. Previously returned handles become disposed; a configure session saved after its state is removed fails with `ObjectDisposedException`.

The source set is fixed for each state runtime. A dynamic named-state addition creates a separate runtime from its registration definition, and removal retires that whole runtime; neither operation changes another runtime's source topology.

## Next steps

* [Profiles](./profiles.md) for persisted catalogs.
* [Application setup](../basic-usage/app-setup.md) for custom source lifecycles.
