---
title: Write routing
description: WriteRoute defaults, per-path WritePlan, conflicts, and multi-write.
---

Writes can target a source independently of read priority. Edits made through `OpenEditSessionAsync` use generated semantic diffs. Generated Patch `SaveAsync` calls apply only the specified operations to the configured write source.

## Default route

`WriteRoute` selects the default root destination. If it is omitted, a patch save infers a unique writable root source. Writable mounted sources own their subtree paths automatically, so nested patches are routed to those sources while unowned properties use the root source. Read-only and `ExplicitOnly` sources do not claim ordinary writes:

```csharp
var userSettings = SourceKey<AppSettings>.Create();
model.Sources(sources => sources.Add(userSettings, CreateUserSettingsSource()));
model.WriteRoute = StateWriteRoute.To(userSettings);
```

JSON file registrations can opt out of inferred ownership while remaining writable through `Source(...)`:

```csharp
sources.JsonFile("database.json")
    .Mount(settings => settings.Database)
    .ExplicitOnly();
```

Overlapping writable mounted ownership paths fail during state creation. Mark one source `ExplicitOnly()` when it should only receive explicit source writes.

When no `WriteRoute` is configured, semantic edit sessions evaluate writable sources in read-priority order for changed paths without an explicit path owner. Configlue simulates each candidate together with any explicitly routed patches against the full source set and uses the first candidate that realizes the requested effective model. Candidate evaluation does not write; the chosen sources are written only after the edit baseline revisions are checked again. A configured `WriteRoute` and explicit path routes remain fixed and fail with `StateConflictException` if they cannot realize the edit. A whole-model replacement Patch is applied to one selected source.

## Per-path plans

Set `ConfiglueModelBuilder<T>.WritePlan` to declare default owners for paths or subtrees; for example, route `Database` to a writable user overlay while leaving unrelated values in lower-priority sources. The most specific path wins. Use typed `SourceKey<T>` values and property selectors to configure routes without string IDs or property paths. A per-operation `StateWritePlan` replaces registration routes for matching paths and can split nested model changes across source fragments:

```csharp
// Route selected model paths to different writable sources for one edit.
var database = SourceKey<AppSettings>.Create();
var secrets = SourceKey<AppSettings>.Create();
var editSessions = context.GetEditSessions<AppSettings>();
var writePlan = StateWritePlan.For<AppSettings>()
    .Route(settings => settings.Database, database)
    .Route(settings => settings.Database!.Password, secrets)
    .Build();
using var routedEdit = await editSessions.OpenEditSessionAsync(writePlan);
routedEdit.Value.Database!.Password = "updated";
var receipt = await routedEdit.CommitAsync();
foreach (var sourceWrite in receipt.Sources)
    Console.WriteLine($"{sourceWrite.SourceId}: {sourceWrite.Revision}");
```

Generated patch saves follow registered routes and recursively split nested patches across multiple sources. Whole-model replacement patches apply to one write destination.

## Verification and conflicts

Plans validate paths and targets before editing, then verify the fully resolved model and all source revisions before writing. The returned `StateWriteReceipt.Sources` reports per-source revisions and physical write count. Writes across different resources are not atomic.

Conflicts fail with `StateConflictException`:

* A read-only contribution shadows the requested value — the exception carries the changed paths and contributing read-only sources.
* An edit requires changing values owned by another source or is hidden by a higher-priority source.
* `Append`/`SetUnion` edits are rebased onto each target source's collection segment; edits that cannot be rebased fail.

## Next steps

* [Mount and project](./mount-and-project.md).
* [Storage migration](../migration/storage-migration.md) for retiring sources after verified moves.
