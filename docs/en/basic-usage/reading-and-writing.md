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

Core exposes asynchronous reads through `ReadAsync` and `GetValueAsync`; it does not provide a synchronous `CurrentValue` property. In DI, the opt-in `Configlue.Extensions.MSOptions` package supplies `IOptions<T>`, `IOptionsSnapshot<T>`, and `IOptionsMonitor<T>` adapters. Their synchronous getters block while asynchronous sources are read, so use `GetValueAsync` in asynchronous flows.

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

The strategy clones public read results, creates the edit-session draft and baseline, and isolates each change-listener value. It must not mutate the input and must return a distinct model whose mutable members do not alias the input. The generated strategy remains the default when this option is omitted. `SaveAsync` and `ApplyPatchesAsync` accept fragments directly, so custom mutable values in those fragments must be copied by the caller.

## Save sparsely

The generated Patch overload writes only the members you specify to the configured write source:

```csharp
await options.SaveAsync(patch => patch.SomeSetting = newValue);
```

Unchanged fields retain their existing sparse state. For destructive replacement of one source contribution, use its typed source handle:

```csharp
var userKey = SourceKey<AppSettings>.Create(); // reuse this key when registering the user source
var replacement = new AppSettings.Patch();
replacement.Name = "new-name";
await options.Source(userKey).ReplaceAsync(replacement);
```

## Edit sessions

Use `OpenEditSessionAsync` on `IConfiglueOptions<T>` when a settings screen applies several changes together. `IWritableOptions<T>` stays focused on patch saves. The session is in-memory until `CommitAsync`; discard it to abandon changes. Disposing during a commit lets it finish and prevents further edits or commits. Sessions compare the full source revision vector immediately before committing and fail with `StateConflictException` if any participating source changed underneath.

Synchronous callers can use `options.OpenEditSession()`. It blocks while asynchronous sources are read; use `OpenEditSessionAsync` from asynchronous code.

```csharp
using var edit = await options.OpenEditSessionAsync();
edit.Update(value => value.SomeSetting = newValue);
// edit.ResetToLoaded();  // restore the value loaded when the session began
// edit.ResetToDefault(); // restore model defaults
await edit.CommitAsync();
```

`Value` and `CurrentValue` expose the draft directly. `Update` edits it in place; the reset helpers restore the whole draft or selected members from the loaded snapshot or a fresh model-default value. A successful commit leaves the session reusable, and each later commit records changes since the previous successful commit. Sessions returned by `OpenEditSessionAsync` know both reset baselines. A session constructed directly with the two-argument constructor has no model-default baseline, so `ResetToDefault` requires the overload that supplies one.

A per-operation `StateWritePlan` can split the session across sources — see [Write routing](../layering/write-routing.md).

## Patches

Generated `TModel.Patch` values address members individually. `Unset` removes only the write source's contribution and exposes lower-priority values again:

```csharp
await options.SaveAsync(patch => patch.Database.Host = "db.example.test");
await options.SaveAsync(patch => patch.Database.Password.Unset());

var patch = new AppSettings.Patch();
patch.SomeSetting = newValue;   // set
// patch.SomeSetting.Unset();   // withdraw this source's contribution
await options.SaveAsync(patch);
```

Use `ApplyPatchesAsync` with `StateSourcePatch` entries for an explicit source-local multi-write. Disjoint section updates sharing a `ResourceId` persist with one physical write; overlapping scopes are rejected and the result reports each source revision and physical write count. Writes across different resources are not atomic.

For one source, use a typed key and handle. `SaveAsync` keeps unspecified contributions in that source; `ReplaceAsync` withdraws unspecified members while preserving explicit Set operations:

```csharp
var userKey = SourceKey<AppSettings>.Create(); // same key used by source registration
var userSource = options.Source(userKey);
await userSource.SaveAsync(patch => patch.Database.Host = "db.example.test");
await userSource.ReplaceAsync(patch => patch.Database.Host = "db.example.test");
```

The common preset exposes semantic selectors for its standard file layers, so those sources do not need application-defined keys:

```csharp
using Configlue.Source.Common;

await options.Source(CommonSource.Local).SaveAsync(
    new AppSettings.Patch { Name = FragmentOperation<string>.Set("local-name") }
);
```

JSON, YAML, and XML file sources can also be selected by normalized path, with an optional document section:

```csharp
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;

await options.Source(JsonFileSource.At("./settings.json")).SaveAsync(patch);
await options.Source(YamlFileSource.At("./settings.yaml", "App:Settings")).SaveAsync(patch);
await options.Source(XmlFileSource.At("./settings.xml", "App:Settings")).SaveAsync(patch);
```

These path-derived selectors match sources registered without an explicit `Id`. JSON selectors also accept the model path of a mounted source through `mountPath`, for example `JsonFileSource.At("./secrets.json", mountPath: "Secrets")`. If you provide an explicit ID, select it with the corresponding `SourceKey<TModel>`.

## Next steps

* [Application setup](./app-setup.md) for DI/non-DI lifetimes and ownership.
* [Write routing](../layering/write-routing.md) for multi-source edits and conflicts.
