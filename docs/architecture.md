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

The foundation is in place: backend-neutral read/write/watch contracts, prioritized source resolution, revision-aware file resources, generated sparse fragments with merge and patch operations, JSON/XML/YAML codecs, and DI registrations for asynchronous read and save. The options runtime merges fragments on reads and saves a complete fragment to an independently selected source.

Reload notifications and edit sessions, validation, schema and storage migrations, named configuration profiles, and provider-specific watcher and backup policies still need implementation. The current API is an architectural foundation rather than a feature-complete replacement for Configuration.Writable.
