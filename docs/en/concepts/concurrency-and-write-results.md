---
title: Concurrency and write results
description: Understand revision checks, edit conflicts, multi-source writes, and StateWriteReceipt.
---

Configlue can combine several logical sources and several physical resources in one effective model. A save therefore needs to distinguish logical source results from physical writes.

## Application writes return StateWriteReceipt

`SaveAsync`, source-local saves, routed patch saves, and edit-session commits return `StateWriteReceipt`.

```csharp
var receipt = await options.SaveAsync(patch =>
{
    patch.Server.Port = 9000;
});

foreach (var source in receipt.Sources)
{
    Console.WriteLine($"{source.SourceId}: {source.Revision}");
}

Console.WriteLine($"Physical writes: {receipt.PhysicalWriteCount}");
```

`Sources` contains one result for each logical source that was written. `PhysicalWriteCount` reports how many backing resources were actually written. Several logical updates can share one physical resource write, for example when disjoint sections live in the same file.

`Revision` is a convenience property for receipts containing exactly one logical source. A no-op returns an empty receipt with zero physical writes.

## Optimistic concurrency

Providers use `RevisionCondition` to express write preconditions. `Match(revision)` requires the observed revision, `MustNotExist` requires the target to be absent, and `None` performs an unchecked write.

Application code usually encounters this behavior through a higher-level operation rather than constructing the condition directly. An edit session reads a baseline, resolves the latest state before commit, and rejects conflicting changes with `StateConflictException` by default.

Set `WriteConflictResolution = WriteConflictResolution.LastWriteWins` on a model registration only when the application intentionally wants an edit-session value to win for members changed concurrently. Destination revision checks still apply to the final write.

## Multi-resource writes are not transactions

A logical save can touch several physical resources. Configlue preserves partial-write information when such an operation fails, but it does not claim transaction atomicity across independent resources.

Code that needs all-or-nothing semantics across several external systems must provide that transaction boundary outside Configlue, or store the affected logical sources in one resource that supports a single physical update.

See [Reading, sessions, and patches](../basic-usage/reading-and-writing.md) for edit sessions and [Write routing](../layering/write-routing.md) for multi-source destination plans.
