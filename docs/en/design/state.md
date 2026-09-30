---
title: "Design: State"
description: The read/write state API, profiles, and DI adapters.
---

State is the read/write facade apps use. Regular reads and writes stay small; advanced operations have focused capability interfaces.

## Read and write facades

- `IReadOnlyState<T>`: async reads (`GetValueAsync`) and `OnChange`.
- `IWritableState<T>`: adds patch-based saves.
- `IConfiglueInspection<T>`: operational source checks and final resolution status.
- `IConfiglueEditSessions<T>`: editing drafts and committing changes.
- `IConfiglueSources<T>`: source-local saves, replacement, batches, and migration.
- `IConfiglueDiagnostics<T>`: topology and reload-failure notifications.

Core has no synchronous current-value property. Use `GetInspection<T>()`, `GetEditSessions<T>()`, `GetSources<T>()`, or `GetDiagnostics<T>()` on a context, or inject the corresponding focused interface in DI. Named registrations expose the same interfaces as keyed services. The opt-in `Configlue.Extensions.MSOptions` package adapts to Microsoft's synchronous options interfaces.

Before saving, the full source revision vector is compared; if any participating source changed, the save stops with `StateConflictException`. Edits shadowed by read-only values stop here too.

File and HTTP source registrations generate stable opaque IDs from their normalized resource descriptors when `Id` is omitted. Set an explicit `Id` only when an integration needs continuity for migration or an external provenance reference.

## Named worlds

- Dynamic states: `model.EnableDynamicStates = true` grows and shrinks named instances at runtime. `GetStateRegistry<T>()` with `TryAdd`/`TryRemoveAsync` is the entry point — for multi-document setups like per-tenant settings.
- Persisted profiles: `EnableProfiles` plus `ConfigureSources` persist named profiles, with a catalog source and an active name. Removing a profile keeps its backing data.
- DI adapters: class models get `IOptions<T>`/`IOptionsSnapshot<T>`/`IOptionsMonitor<T>` adapters. Dynamic names resolve through the registry and `IOptionsMonitor`.

## Diagnostics and logging

`GetDiagnostics()` snapshots that state instance's source topology: source ID, priority, read/write/watch capabilities, physical origin, resource identity, active state, plus default and per-path write routes. Configuration values never reach logs; structured metadata carries model, state name, and source ID.

App setup as a whole lives in [app setup](../basic-usage/app-setup.md); profile operations in [profiles](../profiles/profiles.md).
