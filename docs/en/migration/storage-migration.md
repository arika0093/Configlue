---
title: Storage migration
description: Copy contributions between sources with verification and retirement.
---

# Storage migration

Schema migration evolves shapes; storage migration moves contributions between sources — a new file location, a format change, or a layer consolidation.

## Single source copy

`IConfiglueSources<T>.MigrateSourceAsync(sourceKey, targetKey)` copies one source contribution, applies its schema migration chain, and writes it to a selected destination. Use `SourceKey<T>.Named("legacy")` and `SourceKey<T>.Named("current")` for stable application-defined logical names; opaque provider-generated IDs remain useful for diagnostics but are not needed by application callers.

## Multi-target migration with retirement

`MigrateSourcesToTargetsAsync(sourceIds, targetProjections)` merges only the selected contributions, applies a fragment projection for each destination, and revision-checks and verifies each target. On retry, it re-reads the selected sources and rechecks every target, including targets recorded as complete in the journal. It skips a target write only when that target already matches the projection of the current source contribution; if the source changed between runs, it reconciles the target to the new contribution. If a later target fails, rerun the migration to resume. Multi-target writes are not atomic.

For retries across process restarts, use `StateStorageMigrationDefinition<TFragment>` with `FileStateStorageMigrationJournal`. It stores `StateStorageMigrationProgress` in one JSON file per migration ID and updates the file after each target verifies. `FileResource` replaces each file atomically with a revision check, and the journal holds a cross-process lease for the full migration run so another process using the same ID waits. Custom journals can implement `IStateStorageMigrationLeaseProvider` for the same behavior; coordinate runs externally when a journal does not provide leases.

For a JSON-to-YAML file migration, both sources decode to the same generated Fragment; the target writer handles the format change:

```csharp
using Configlue.Migrations;

var migration = new StateStorageMigrationDefinition<AppSettings.Fragment>(
    "settings-json-to-yaml-v1",
    ["legacy-json"],
    [new StateStorageMigrationTarget<AppSettings.Fragment>("settings-yaml", fragment => fragment)],
    retireSources: true);

var journal = new FileStateStorageMigrationJournal("./.configlue-migrations");
var progress = await ((IConfiglueSources<AppSettings>)options).MigrateAsync(migration, journal);
```

Before building options on the next startup, call `journal.ReadAsync(migration.Id)`. If `SourcesRetired` is true, omit the legacy JSON source from registration. Calling `MigrateAsync` with the same definition returns the retired progress without reading the old source. If migration stopped partway through, register the old source and resume with the same journal.

For a split into multiple files, declare a projection for each target. Only the selected source contribution is merged before each subtree is projected:

```csharp
using Configlue.Migrations;

var migration = new StateStorageMigrationDefinition<AppSettings.Fragment>(
    "settings-json-to-split-files-v1",
    ["legacy-json"],
    [
        new("database-file", fragment => new AppSettings.Fragment
        {
            Database = fragment.Database,
        }),
        new("ui-file", fragment => new AppSettings.Fragment
        {
            Ui = fragment.Ui,
        }),
    ],
    retireSources: true);
```

Pass `retireSources: true` to remove the selected sources from that options instance after every target verifies and only when virtual resolution proves the effective model stays the same; the result lists them in `RetiredSourceIds`. This changes the running options topology; it does not delete backing data, so remove retired sources from the application's registration for future process starts.

## Practical notes

* Keep the original data until target verification succeeds; if migration fails, retry with the same source, target definitions, and journal.
* Remove old files only through an explicit application decision — retirement never deletes backing data.
* For format changes (e.g. JSON to YAML), model the old representation as a read-only fallback input and migrate its source ID into the new writable target.

## Next steps

* [Schema migration](./schema-migration.md).
* [Adopting Configuration.Writable](./adopting-configuration-writable.md) for the legacy-codec recipe.
