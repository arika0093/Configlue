# Architecture

Configlue follows the source and fragment designs captured in [Configuration.Writable issue #113](https://github.com/arika0093/Configuration.Writable/issues/113) and [issue #118](https://github.com/arika0093/Configuration.Writable/issues/118).

## Boundaries

- A **source** contributes a logical configuration snapshot and can independently expose read, write, and watch capabilities.
- A **resource** represents a physical endpoint such as a file, database record, or HTTP response.
- A **codec** translates bytes to and from typed values without performing resource I/O.
- A generated **fragment** preserves whether each model member is missing or present, including a present `null` or default value.
- Resolution, migration, projection, and write planning operate on fragments; application code edits ordinary model values.

Logical schema and physical storage topology are independent. Multiple sources can contribute to one model subtree, and multiple bindings can share one resource.
Each logical source may expose a `ResourceId` for that physical resource. Section views, ZIP entry views, and projected sources preserve this identity so later write coordination can group logical updates that share storage.
Explicit `ApplyPatchesAsync` calls plan source-local patches before persistence, group compatible section mutations by `ResourceId`, and commit each resource once. Backends that cannot batch a shared resource fail before any group is written; scope overlap also fails during planning.

## Implementation status

The foundation is in place: backend-neutral read/write/watch contracts, prioritized source resolution, source-contract migrations and projections, property-level provenance explanations, a read-only environment-variable source for generated sparse fragments, JSON/XML/YAML section resources, ZIP archive entry resources, selected source-to-source migration, retryable multi-source to multi-target storage migration with per-target projections and verification, optional verified logical source retirement, explicit source-local multi-write patching grouped by resource, revision-aware file resources with backup rotation and restore, generated sparse fragments with merge, semantic diff, and patch operations, JSON/XML/YAML codecs, JSON Schema export from generated model schemas and JSON type metadata, per-source schema migration chains, configurable save validation, keyed DI profiles, a runtime profile registry, Microsoft options adapters for class models, debounced change notifications, and registrations for asynchronous read, save, and revision-vector-checked edit sessions. The options runtime migrates and merges fragments on reads, watches participating sources, validates writes, and applies sparse semantic diffs to the selected source by default. A `StateWritePlan` can route changed paths to multiple writable sources, including splitting nested model changes across source fragments. `Append` and `SetUnion` edits are planned against each target's collection segment while preserving other contributions.

Edits that their planned sources cannot realize because they would remove values owned by another source or be hidden by higher-priority contributions fail with `StateConflictException` before persistence begins. Writes across different resources are not atomic. Source retirement is scoped to the current options instance and leaves backing data intact; callers must update source registration for future process starts. Provider-specific watcher policies still need implementation. The current API is an architectural foundation rather than a feature-complete replacement for Configuration.Writable.
