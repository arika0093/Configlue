---
title: Profiles
description: Persisted named profiles with an active-profile catalog.
---

# Profiles

Persistent named profiles use a separate writable source for their catalog. The profile source factory receives each profile name, which lets an application store profile values in separate files, sections, or other resources.

```csharp
model.EnableProfiles(profileCatalogSource, defaultProfileName: "default");
model.SourcesForOptions((profileName, sources) =>
    sources.FromJsonFile(new()
    {
        Id = "profile-state",
        Path = Path.Combine(profileDirectory, profileName + ".json"),
    }));

var profiles = context.GetProfiledOptions<AppSettings>();
await profiles.CreateProfileAsync("work", copyFrom: "default");
await profiles.SetActiveProfileAsync("work");
var active = await profiles.GetActiveValueAsync();
await profiles.SaveAsync(settings => settings.RetryCount++);
var current = profiles.CurrentValue; // synchronous; blocks while sources are read
await profiles.RemoveProfileAsync("work");
```

The profile facade can read and save the active profile directly; save overloads accept a full value, a synchronous update, or an asynchronous update. Its `CurrentValue` property blocks while sources are read, so prefer `GetActiveValueAsync` from asynchronous flows. The profile catalog source must be writable and remains caller-owned. The catalog is stored through a normal writable `StateSource<ConfiglueProfileCatalog>`, so its provider can be chosen independently. `IConfiglueProfiledOptions<TModel>` lazily restores or creates the default profile on its first async operation, can copy a profile with `CreateProfileAsync`, and persists active-profile changes. Removing a profile removes it from the catalog and runtime; its backing state is retained.

`OnChange` follows the active profile: it reports value changes and emits the newly active value after a profile switch. When the catalog source provides a watcher, the manager observes external catalog changes too. Dispose the subscription to stop its callbacks. The manager's catalog watcher stops when the owning context is disposed; dispose a directly constructed profile manager yourself.

For DI, use the same `EnableProfiles` and `SourcesForOptions` calls inside `services.AddConfiglue(...)`, then resolve `IConfiglueProfiledOptions<AppSettings>` from the provider. Profile names added after provider construction resolve through `IOptionsMonitor` and `IConfiglueOptionsRegistry`, not keyed services. The one-arity non-DI entry is `context.GetProfiledOptions<AppSettings>()` as shown above.

```csharp
services.AddSingleton<ProfileCatalogStore>();
services.AddConfiglueProfiledOptions<AppConfig, AppConfig.Fragment>(
    (provider, profileName) => CreateProfileSources(provider, profileName),
    provider =>
    {
        var catalogStore = provider.GetRequiredService<ProfileCatalogStore>();
        return new StateSource<ConfiglueProfileCatalog>("profile-catalog", catalogStore, writer: catalogStore);
    });
```

Profile names are also names in the options registry and must not collide with fixed `OptionsName` registrations. `SourcesForOptions` runs for each constructed named runtime, including the fixed registration itself, and receives that runtime's exact `OptionsName` — use it to build profile-specific sources. Named profiles can use keyed DI registrations, for example `AddConfiglueOptions<TModel, TModel.Fragment>("profile", sourceSet)` and `GetRequiredKeyedService<IReadOnlyOptions<TModel>>("profile")`.

## Next steps

* [Named instances and dynamic options](./dynamic-options.md) for runtime-only named instances without persistence.
* [Storage migration](../migration/storage-migration.md).
