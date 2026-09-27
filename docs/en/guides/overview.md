---
title: About the functional guides
description: Find articles by what you want to do.
---

# About the functional guides

The functional guides are a dictionary, not a course. After the tutorial, start from what you want to do. No reading order required.

## Setup and read-write

| To do this | Read |
| --- | --- |
| Register with or without DI; lifetimes and ownership (configuration method) | [Application setup](../basic-usage/app-setup.md) |
| Choose between reads, edit sessions, and patches | [Reading, sessions, and patches](../basic-usage/reading-and-writing.md) |
| Use the standard shared/local/selected/environment/command-line shape (location presets) | [Common layered sources](../basic-usage/common-sources.md) |

## Locations and formats

| To do this | Read |
| --- | --- |
| Mix JSON, YAML, and XML formats with file saving and section names | [Files, formats, and sections](../sources/files-and-sections.md) |
| Layer read-only environment and command-line sources | [Environment and command line](../sources/environment-and-commandline.md) |
| Source settings from HTTP and ZIP resources | [HTTP and ZIP](../sources/http-and-zip.md) |
| Fall back across formats and build custom sources | [Fallback and custom sources](../sources/fallback-and-custom.md) |

## Layering

| To do this | Read |
| --- | --- |
| Understand priority, resolution, and merge modes | [Resolution and merge](../layering/resolution-and-merge.md) |
| Decide destinations by default and per path (write routing) | [Write routing](../layering/write-routing.md) |
| Lend part of the model to another source | [Mount and project](../layering/mount-and-project.md) |

## Named instances and validation

| To do this | Read |
| --- | --- |
| Share one type across names (instance names) and grow them at runtime | [Named instances and dynamic options](../profiles/dynamic-options.md) |
| Persisted profiles with active-profile switching | [Profiles](../profiles/profiles.md) |
| External-change detection, debouncing, and validation (change detection and validation) | [Changes and validation](../basic-usage/changes-and-validation.md) |

## Schema and migration

| To do this | Read |
| --- | --- |
| JSON Schema export and test doubles | [JSON Schema and testing](../advanced/json-schema-and-testing.md) |
| Version up and convert old shapes (schema migration) | [Schema migration](../migration/schema-migration.md) |
| Move storage and retire old sources (storage migration) | [Storage migration](../migration/storage-migration.md) |
| Adopt existing `Configuration.Writable` files | [Adopting Configuration.Writable](../migration/adopting-configuration-writable.md) |

## Operations and diagnostics

| To do this | Read |
| --- | --- |
| Backup generations, restore, logging, explanations, and diagnostics | [Backups, logging, and diagnostics](../advanced/backups-and-observability.md) |
| Run trimming-safe on NativeAOT | [NativeAOT](../advanced/native-aot.md) |

To understand from the mechanics up, see the [design overview](../design/overview.md).
