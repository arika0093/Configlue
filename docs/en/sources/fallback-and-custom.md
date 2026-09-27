---
title: Fallback and custom sources
description: Group equivalent representations and compose resources with codecs.
---

# Fallback and custom sources

## Fallback sources

Use `FallbackStateSource<TFragment>` to group serialized representations of the same logical state, such as a canonical JSON file and a legacy YAML file. It reads the first successful candidate by priority, subject to each candidate's fallback condition, and exposes that candidate as one source — values from separate formats are never overlaid.

By default, writes go to the active writable candidate, or the highest-priority writable candidate when none is active; set `writeSourceId` to route edits to a fixed candidate such as the canonical file. This does not copy state on creation or delete the other representations, and the candidate sources and resources remain caller-owned.

Copy the selected value to a missing, higher-priority writable candidate explicitly with `PromoteAsync("canonical")`. Promotion checks the selected source revision again and conditionally writes against the target's missing-state revision. If the target is already active, the call returns with `WasAlreadyPromoted` set. Existing target state is never overwritten, and promotion does not remove the source representation. Since the source and target may be separate resources, this operation cannot make their updates transactional; a source change immediately after the final revision check can still race with the copy.

## Custom sources

Use `SerializedStateSource.FromResource<T>` to compose a resource and a codec into a typed source with automatic writer and watcher detection:

```csharp
var currentSource = SerializedStateSource.FromResource<AppSettings.Fragment>(
    "current",
    fileResource,
    new JsonStateCodec<AppSettings.Fragment>());
```

Resources can expose a stable `ResourceId` separately from logical source IDs and physical-origin labels; section views inherit the underlying identity, and custom resources can implement `IResourceIdentity` or supply an ID to `SerializedStateSource.FromResource`, `StateSource`, or a section view.

For a fully hand-rolled source, implement `IStateReader<TFragment>` (plus `IStateWriter<TFragment>` / `IStateWatcher` as needed) and add it with `Sources(sources => sources.Add(existingSource))` or the DI `(provider, sources) => ...` overload with `sources.Add(id, reader, priority, fallbackCondition)`.

### Combine multiple resources as one logical source

Use `CompositeStateSource<TFragment>` when several resources contribute sparse fragments but should appear as one logical source to the options runtime. Keep writes explicit by naming a default writable component and optional member-path routes:

```csharp
var combined = new CompositeStateSource<AppSettings.Fragment>(
    new StateSourceSet<AppSettings.Fragment>([globalSource, localSource]),
    defaultWriteSourceId: "local",
    writePlan: new StateWritePlan(new Dictionary<string, string>
    {
        ["Policy.Endpoint"] = "global",
    }));
model.Sources(sources => sources.Add(combined.CreateSource("common-files", priority: 100)));
```

The component sources merge from low to high priority. Each component's `fallbackCondition` controls whether a missing or unavailable fragment can be omitted. All successful components in one read must report matching schema metadata; the combined fragment is migrated once. Component revisions and watchers remain nested under the logical source revision. Model edits route changed nested members by the most-specific member path; unsetting a member removes its owner's contribution and reveals lower-priority values. Direct generated patches treat a nested member operation as atomic and cannot split it across descendant routes. Targets must be writable components. The composite has no single `ResourceId`; component writes use the existing resource batching rules. Writes across different resources are sequential and non-atomic. If a later resource fails, `StateMultiWriteException` carries completed source results, the failed resource and sources, unattempted source IDs, and the original exception in `InnerException`.

## Next steps

* [Resolution and merge](../layering/resolution-and-merge.md).
* [Storage migration](../migration/storage-migration.md) for moving contributions between sources.
