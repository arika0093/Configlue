---
title: Reading, sessions, and patches
description: GetValueAsync, SaveAsync, edit sessions, and patches.
---

# Reading, sessions, and patches

## Read the current value

```csharp
var setting = await options.GetValueAsync();
Console.WriteLine($">> Name: {setting.Name}");
```

Reads resolve every source by priority and return a deep copy. In DI you can also use the synchronous `IOptions<T>.Value` / `IOptionsSnapshot<T>` / `IOptionsMonitor<T>` adapters, but prefer the async methods in asynchronous flows.

For synchronous callers, `options.CurrentValue` reads the sources and returns a deep copy. The getter blocks until asynchronous source reads complete. In DI, `IOptionsMonitor<T>.CurrentValue` uses its watcher-backed cache when the registered sources expose watchers.

Generated clones handle nested Configlue models and common collections. If a model contains a mutable reference type that needs a custom copy, configure one strategy for that options runtime:

```csharp
config.Add<AppSettings>(model =>
{
    model.UseCloneStrategy(static original =>
    {
        var clone = original.DeepClone();
        clone.CustomState = original.CustomState?.DeepClone();
        return clone;
    });
});
```

The strategy clones public read results, creates the edit-session draft and baseline, isolates each change-listener value, and clones full model values before save fragments are created. It must not mutate the input and must return a distinct model whose mutable members do not alias the input. The generated strategy remains the default when this option is omitted. `ApplyPatchAsync` and `ApplyPatchesAsync` accept fragments directly, so custom mutable values in those fragments must be copied by the caller.

## Save sparsely

The updater overload edits a deep clone of the current value and writes only the changed paths as a semantic diff:

```csharp
await options.SaveAsync(settings => settings.SomeSetting = newValue);
```

Unchanged fields retain their existing sparse state. Contrast with the whole-value overload, which replaces the write source's complete contribution — including model defaults — and does not preserve members absent from that source:

```csharp
await options.SaveAsync(updatedConfig); // full replacement of the write target
```

## Edit sessions

Use `BeginConfigureAsync` when a settings screen applies several changes together. The session is in-memory until `SaveAsync`; discard it to abandon changes. Sessions compare the full source revision vector immediately before saving and fail with `StateConflictException` if any participating source changed underneath.

```csharp
using var edit = await options.BeginConfigureAsync();
edit.Value.SomeSetting = newValue;
await edit.SaveAsync();
```

A per-operation `StateWritePlan` can split the session across sources — see [Write routing](../layering/write-routing.md).

## Patches

Generated `TModel.Patch` values address members individually. `Unset` removes only the write source's contribution and exposes lower-priority values again:

```csharp
var patch = new AppSettings.Patch();
patch.SomeSetting = newValue;   // set
// patch.SomeSetting.Unset();   // withdraw this source's contribution
await options.ApplyPatchAsync(patch);
```

Use `ApplyPatchesAsync` with `StateSourcePatch` entries for an explicit source-local multi-write. Disjoint section updates sharing a `ResourceId` persist with one physical write; overlapping scopes are rejected and the result reports each source revision and physical write count. Writes across different resources are not atomic.

## Next steps

* [Application setup](./app-setup.md) for DI/non-DI lifetimes and ownership.
* [Write routing](../layering/write-routing.md) for multi-source edits and conflicts.
