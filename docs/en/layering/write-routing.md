---
title: Write routing
description: WriteRoute defaults, per-path WritePlan, conflicts, and multi-write.
---

# Write routing

Writes can target a source independently of read priority. Edits made through `BeginConfigureAsync` or the updater overloads use generated semantic diffs.

## Default route

`WriteRoute` selects the default destination. Without an owner for a path, the write uses `WriteRoute` (or the highest-priority writable source):

```csharp
model.WriteRoute = StateWriteRoute.To("user-settings");
```

## Per-path plans

Set `ConfiglueModelBuilder<T>.WritePlan` to declare default owners for paths or subtrees; for example, route `Database` to a writable user overlay while leaving unrelated values in lower-priority sources. The most specific path wins. A per-operation `StateWritePlan` replaces registration routes for matching paths and can split nested model changes across source fragments:

```csharp
// Route selected model paths to different writable sources for one edit.
var writePlan = new StateWritePlan(new Dictionary<string, string>
{
    ["Database"] = "database-settings",
    ["Database.Password"] = "secrets",
});
using var routedEdit = await options.BeginConfigureAsync(writePlan);
routedEdit.Value.Database!.Password = "updated";
var writeResult = await routedEdit.SaveAsync();
var sourceWrites = writeResult.MultiWriteResult;
```

The `SaveAsync(value, writePlan)` overload compares the value with the resolved baseline and routes only changed paths.

## Verification and conflicts

Plans validate paths and targets before editing, then verify the fully resolved model and all source revisions before writing. The returned `StateWriteResult.MultiWriteResult` reports per-source revisions and physical write count. Writes across different resources are not atomic.

Conflicts fail with `StateConflictException`:

* A read-only contribution shadows the requested value — the exception carries the changed paths and contributing read-only sources.
* An edit requires changing values owned by another source or is hidden by a higher-priority source.
* `Append`/`SetUnion` edits are rebased onto each target source's collection segment; edits that cannot be rebased fail.

## Next steps

* [Mount and project](./mount-and-project.md).
* [Storage migration](../migration/storage-migration.md) for retiring sources after verified moves.
