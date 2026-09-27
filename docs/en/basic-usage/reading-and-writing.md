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

For synchronous callers, `options.CurrentValue` resolves the sources on first access and returns a deep copy. Later accesses return a clone of the cached value. Successful watcher reloads replace the cache, and successful writes invalidate it so the next access reads again. Without watchers, external changes are not observed automatically; call `GetValueAsync` for a fresh read. The first getter blocks until asynchronous source reads complete. In DI, `IOptionsMonitor<T>.CurrentValue` has its own watcher-backed cache.

Generated clones handle nested Configlue models, common collections, and ordinary POCOs whose public instance state consists of public get/set properties and that have a public parameterless constructor. The generated POCO helpers preserve shared references and cycles between those POCOs. Types with public fields, read-only properties, constructor arguments, or required/init-only properties, and values in unsupported collection shapes, are left as references; configure a custom copy strategy for those values:

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

Use `BeginConfigureAsync` when a settings screen applies several changes together. The session is in-memory until `SaveAsync`; discard it to abandon changes. Disposing during a save lets that save finish and prevents further edits or saves. Sessions compare the full source revision vector immediately before saving and fail with `StateConflictException` if any participating source changed underneath.

Synchronous callers can use `options.BeginConfigure()`. It blocks while asynchronous sources are read; use `BeginConfigureAsync` from asynchronous code.

```csharp
using var edit = await options.BeginConfigureAsync();
edit.Update(value => value.SomeSetting = newValue);
// edit.ResetToLoaded();  // restore the value loaded when the session began
// edit.ResetToDefault(); // restore model defaults
await edit.SaveAsync();
```

`Value` and `CurrentValue` expose the draft directly. `Update` edits it in place; the reset helpers restore the whole draft or selected members from the loaded snapshot or a fresh model-default value. A successful save leaves the session reusable, and each later save records changes since the previous successful save. Sessions returned by `BeginConfigureAsync` know both reset baselines. A session constructed directly with the two-argument constructor has no model-default baseline, so `ResetToDefault` requires the overload that supplies one.

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
