# Architecture

Configlue follows the source and fragment designs captured in [Configuration.Writable issue #113](https://github.com/arika0093/Configuration.Writable/issues/113) and [issue #118](https://github.com/arika0093/Configuration.Writable/issues/118).

## Boundaries

- A **source** contributes a logical configuration snapshot and can independently expose read, write, and watch capabilities.
- A **resource** represents a physical endpoint such as a file, database record, or HTTP response.
- A **codec** translates bytes to and from typed values without performing resource I/O.
- A generated **fragment** preserves whether each model member is missing or present, including a present `null` or default value.
- Resolution, migration, projection, and write planning operate on fragments; application code edits ordinary model values.

Logical schema and physical storage topology are independent. Multiple sources can contribute to one model subtree, and multiple bindings can share one resource.

## Implementation status

The foundation is in place: backend-neutral read/write/watch contracts, prioritized source resolution, source-contract migrations and projections, property-level provenance explanations, JSON section resources, selected source-to-source migration, revision-aware file resources with backup rotation and restore, generated sparse fragments with merge, semantic diff, and patch operations, JSON/XML/YAML codecs, per-source schema migration chains, configurable save validation, keyed DI profiles, a runtime profile registry, debounced change notifications, and registrations for asynchronous read, save, and revision-vector-checked edit sessions. The options runtime migrates and merges fragments on reads, watches participating sources, validates writes, and applies sparse edit diffs to the selected source contribution.

Merge-aware planning across multiple source contributions, grouped multi-source writes, multi-target resumable storage migrations, and provider-specific watcher policies still need implementation. The current API is an architectural foundation rather than a feature-complete replacement for Configuration.Writable.
