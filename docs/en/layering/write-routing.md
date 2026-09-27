---
title: Write routing
description: WriteRoute defaults, per-path WritePlan, conflicts, and multi-write.
---

# Write routing

Writes can target a source independently of read priority. Edits made through `OpenEditSessionAsync` use generated semantic diffs. Generated Patch `SaveAsync` calls apply only the specified operations to the configured write source.

## Default route

`WriteRoute` selects the default destination. Without an owner for a path, the write uses `WriteRoute` (or the highest-priority writable source):

```csharp
model.WriteRoute = StateWriteRoute.To("user-settings");
```

When no `WriteRoute` is configured, semantic edits evaluate writable sources in read-priority order for changed paths without an explicit path owner. Configlue simulates each candidate together with any explicitly routed patches against the full source set and uses the first candidate that realizes the requested effective model. Candidate evaluation does not write; the chosen sources are written only after the edit baseline revisions are checked again. A configured `WriteRoute` and explicit path routes remain fixed and fail with `StateConflictException` if they cannot realize the edit. `SaveAsync(value)` still replaces the selected write source's complete contribution.

## Per-path plans

Set `ConfiglueModelBuilder<T>.WritePlan` to declare default owners for paths or subtrees; for example, route `Database` to a writable user overlay while leaving unrelated values in lower-priority sources. The most specific path wins. A per-operation `StateWritePlan` replaces registration routes for matching paths and can split nested model changes across source fragments:

```csharp
// Route selected model paths to different writable sources for one edit.
var writePlan = new StateWritePlan(new Dictionary<string, string>
{
    ["Database"] = "database-settings",
    ["Database.Password"] = "secrets",
});
using var routedEdit = await options.OpenEditSessionAsync(writePlan);
routedEdit.Value.Database!.Password = "updated";
var writeResult = await routedEdit.CommitAsync();
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
