---
title: Mount and project
description: Bind source fragments into nested models, reversibly when needed.
---

# Mount and project

When the source fragment already matches a generated nested model, mount it with `StateSourceProjection.Mount<TSubtreeFragment, TRootFragment>(source, "Policy")` or register it with the typed selector `sources.AddMounted<TModel, TRootFragment, TSubtreeModel, TSubtreeFragment>(source, model => model.Policy)`. The selector checks both the member path and subtree model/fragment types at compile time; the string overload is available when a path is composed dynamically and validates it against the generated schema during registration.

```csharp
model.Sources(sources =>
{
    sources.FromJsonFile(new() { Id = "settings", Path = "settings.json", Priority = 10 });
    sources.AddMounted<AppSettings, AppSettings.Fragment, PolicySettings, PolicySettings.Fragment>(
        policyHttpSource,
        model => model.Policy,
        root => root.Policy.Value!);
});
```

The full JSON source and the mounted HTTP source can contribute different members of `AppSettings.Policy`; missing HTTP members fall through to JSON. A partial mounted fragment contributes only its present members. The mount retains the source ID, priority, revision, resource identity, and physical origin. It is read-only unless you provide `toSource`, which maps the sparse root contribution back to the source fragment. The callback should preserve the source fragment's partial shape.

For a distinct source DTO, first use `StateSourceProjection.Project` (including source-schema migrations and an explicit reverse projection when writable) to map it to the nested model fragment, then mount that projected source. Use `ProjectWithUpdate` when the reverse projection needs the current source contract to preserve fields outside the projected model; its callback receives the previous and updated projected values plus the current source contract, so it can distinguish an unset from an unprojected field. Current-aware writes re-read and revision-check the source, then prepare batch mutations when the resource supports them. Map `Unset` operations to the source's removal representation or throw from the reverse callback when the source contract cannot represent removal.

For an HTTP DTO that projects only `RetryCount` while retaining source-only fields such as `Endpoint`, use `ProjectWithUpdate` to update the current DTO and preserve those fields. A JSON section can be mounted with `JsonSectionResource(resource, "App:Policy")`; disjoint section mounts that share that resource are combined into one physical write.

Use `StateSourceProjection.Project` to migrate and map a source-specific fragment into a nested model fragment; provide a reverse projection to enable writes.

## Next steps

* [Files and sections](../sources/files-and-sections.md).
* [Profiles](../profiles/profiles.md) for per-name source construction with `SourcesForOptions`.
