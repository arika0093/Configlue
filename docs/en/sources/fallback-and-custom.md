---
title: Fallback and custom sources
description: Group equivalent representations and compose resources with codecs.
---

# Fallback and custom sources

## Fallback sources

Use `FallbackStateSource<TFragment>` to group serialized representations of the same logical state, such as a canonical JSON file and a legacy YAML file. It reads the first successful candidate by priority, subject to each candidate's fallback condition, and exposes that candidate as one source — values from separate formats are never overlaid.

By default, writes go to the active writable candidate, or the highest-priority writable candidate when none is active; set `writeSourceId` to route edits to a fixed candidate such as the canonical file. This does not copy state on creation or delete the other representations, and the candidate sources and resources remain caller-owned.

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

## Next steps

* [Resolution and merge](../layering/resolution-and-merge.md).
* [Storage migration](../migration/storage-migration.md) for moving contributions between sources.
