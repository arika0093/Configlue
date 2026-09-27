---
title: "Design: Options"
description: The read/write facade. Profiles, dynamic options, DI adapters.
---

# Design: Options (facade)

Options is the facade apps see. Regular reads and writes stay small; source administration is available through `IConfiglueOptions<T>`.

## Read and write facades

- `IReadOnlyOptions<T>`: async reads (`GetValueAsync`/`ReadAsync`) and `OnChange`.
- `IWritableOptions<T>`: adds saves and `OpenEditSessionAsync`.
- `IConfiglueOptions<T>`: advanced source diagnostics and explanation, reload failures, source patch batches, and source/storage migration. `CurrentValue` is also available here; it blocks during its first asynchronous read.

Before saving, the full source revision vector is compared; if any participating source changed, the save stops with `StateConflictException`. Edits shadowed by read-only values stop here too.

## Named worlds

- Dynamic options: `model.EnableDynamicOptions = true` grows and shrinks named instances at runtime. `GetOptionsRegistry<T>()` with `TryAdd`/`TryRemoveAsync` is the entry point — for multi-document setups like per-tenant settings.
- Persisted profiles: `EnableProfiles` plus `SourcesForOptions` persist named profiles, with a catalog source and an active name. Removing a profile keeps its backing data.
- DI adapters: class models get `IOptions<T>`/`IOptionsSnapshot<T>`/`IOptionsMonitor<T>` adapters. Dynamic names resolve through the registry and `IOptionsMonitor`.

## Diagnostics and logging

`GetDiagnostics()` snapshots that options instance's source topology: source ID, priority, read/write/watch capabilities, physical origin, resource identity, retired status, plus default and per-path write routes. Configuration values never reach logs; structured metadata carries model, options name, and source ID.

App setup as a whole lives in [app setup](../basic-usage/app-setup.md); profile operations in [profiles](../profiles/profiles.md).
