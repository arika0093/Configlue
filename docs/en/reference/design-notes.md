---
title: Design notes
description: Project boundaries, current implementation status, and known limitations.
---

# Design notes

Configlue follows the source-and-fragment direction explored for Configuration.Writable: backend-neutral state, sparse generated fragments, and logical schema kept independent from physical storage topology.

## Boundaries

* A **source** contributes a logical configuration snapshot and can independently expose read, write, and watch capabilities.
* A **resource** represents a physical endpoint such as a file, a ZIP entry, or an HTTP response.
* A **codec** translates bytes to and from typed values without performing resource I/O.
* A generated **fragment** preserves whether each model member is missing or present, including a present `null` or default value.
* Resolution, migration, projection, and write planning operate on fragments; application code edits ordinary model values.

Multiple sources can contribute to one model subtree, and multiple bindings can share one resource. Each logical source may expose a `ResourceId` for that physical resource. Section views, ZIP entry views, and projected sources preserve this identity so later write coordination can group logical updates that share storage. Backends that cannot batch a shared resource fail before any group is written; scope overlap also fails during planning.

## Project map

Projects live directly under `src/`. `Configlue` is a no-assembly meta-package that brings in Core, DI integration, the JSON provider, HTTP resources, the environment source, and the source-generator analyzer. `Configlue.Abstraction` holds the contracts; `Configlue.Core` holds the resolution and persistence runtime including the general file resource. `Configlue.Extensions.DI` contains DI registration and Microsoft options adapters. `Configlue.Generator` emits sparse fragments and patches. `Configlue.Provider.Json`, `.Xml`, and `.Yaml` contain format codecs, section resources, and file registrations. `Configlue.Source.Environment` maps process variables to fragments; `Configlue.Source.CommandLine` maps `System.CommandLine` parse results; `Configlue.Source.Common` composes the standard layered preset. `Configlue.Resource.Http` transports resource bytes with ETag revisions; `Configlue.Resource.Http.AspNetCore` serves them; `Configlue.Resource.Zip` exposes archive entries. `Configlue.Testing` provides in-memory doubles.

## Implementation status

The foundation is in place: backend-neutral read/write/watch contracts, prioritized source resolution, source-contract migrations and projections, property-level provenance explanations, source-topology diagnostics snapshots, optional structured logging without configuration values, JSON/XML/YAML section resources, ZIP entry resources, selected source-to-source migration, retryable multi-source to multi-target storage migration with per-target projections and verification, optional verified logical source retirement, explicit source-local multi-write patching grouped by resource, revision-aware file resources with backup rotation and restore, generated sparse fragments with merge, semantic diff, and patch operations, JSON/XML/YAML codecs, JSON Schema export, per-source schema migration chains, configurable save validation, keyed DI profiles, runtime profile and options registries, a persistent named-profile catalog, Microsoft options adapters, debounced change notifications, and registrations for asynchronous read, save, and revision-vector-checked edit sessions.

## Known limitations

* Writes across different resources are not atomic.
* Source retirement is scoped to the current options instance and leaves backing data intact; callers must update source registration for future process starts.
* Provider-specific watcher policies still need implementation.
* No custom merge-strategy registration yet — see `todo/custom-merge-strategies.md`.
* No declarative, restart-spanning storage-migration definitions yet — see `todo/declarative-storage-migration.md`.

The current API is an architectural foundation rather than a feature-complete replacement for Configuration.Writable.
