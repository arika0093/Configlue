---
title: "Design: Options"
description: The read/write facade. Profiles, dynamic options, DI adapters.
---

# Design: Options (facade)

Options is the facade apps see. Source and Fragment details stay hidden; only read, save, watch, explain, and diagnose show through.

## Read and write facades

- `IReadOnlyOptions<T>`: synchronous `CurrentValue`, async reads (`GetValueAsync`/`ReadAsync`), `OnChange`, `ExplainAsync`, and `GetDiagnostics`. `CurrentValue` blocks for async sources; async application flows should use `GetValueAsync`.
- `IWritableOptions<T>`: adds saves (`SaveAsync`, `BeginConfigureAsync`), `ApplyPatchAsync`/`ApplyPatchesAsync`, and source/storage migration.

Before saving, the full source revision vector is compared; if any participating source changed, the save stops with `StateConflictException`. Edits shadowed by read-only values stop here too.

## Named worlds

- Dynamic options: `model.EnableDynamicOptions = true` grows and shrinks named instances at runtime. `GetOptionsRegistry<T>()` with `TryAdd`/`TryRemoveAsync` is the entry point — for multi-document setups like per-tenant settings.
- Persisted profiles: `EnableProfiles` plus `SourcesForOptions` persist named profiles, with a catalog source and an active name. Removing a profile keeps its backing data.
- DI adapters: class models get `IOptions<T>`/`IOptionsSnapshot<T>`/`IOptionsMonitor<T>` adapters. Dynamic names resolve through the registry and `IOptionsMonitor`.

## Diagnostics and logging

`GetDiagnostics()` snapshots that options instance's source topology: source ID, priority, read/write/watch capabilities, physical origin, resource identity, retired status, plus default and per-path write routes. Configuration values never reach logs; structured metadata carries model, options name, and source ID.

App setup as a whole lives in [app setup](../basic-usage/app-setup.md); profile operations in [profiles](../profiles/profiles.md).
