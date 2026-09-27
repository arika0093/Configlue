---
title: Storage migration
description: Copy contributions between sources with verification and retirement.
---

# Storage migration

Schema migration evolves shapes; storage migration moves contributions between sources — a new file location, a format change, or a layer consolidation.

## Single source copy

`IWritableOptions<T>.MigrateSourceAsync(sourceId, targetId)` copies one source contribution, applies its schema migration chain, and writes it to a selected destination.

## Multi-target migration with retirement

`MigrateSourcesToTargetsAsync(sourceIds, targetProjections)` merges only the selected contributions, applies a fragment projection for each destination, and revision-checks and verifies each target. A completed target is skipped on retry; if a later target fails, rerun the migration to resume. Multi-target writes are not atomic.

Pass `retireSources: true` to remove the selected sources from that options instance after every target verifies and only when virtual resolution proves the effective model stays the same; the result lists them in `RetiredSourceIds`. This changes the running options topology; it does not delete backing data, so remove retired sources from the application's registration for future process starts.

## Practical notes

* Keep the original data until target verification succeeds; if migration fails, retry with the same source and target definitions.
* Remove old files only through an explicit application decision — retirement never deletes backing data.
* For format changes (e.g. JSON to YAML), model the old representation as a read-only fallback input and migrate its source ID into the new writable target.

## Next steps

* [Schema migration](./schema-migration.md).
* [Adopting Configuration.Writable](./adopting-configuration-writable.md) for the legacy-codec recipe.
