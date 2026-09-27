---
title: Adopting Configuration.Writable
description: Read legacy inline-versioned files as migration inputs.
---

# Adopting Configuration.Writable

You can adopt Configlue while keeping files written by [Configuration.Writable](https://github.com/arika0093/Configuration.Writable). The legacy files stay untouched; Configlue reads them through an opt-in decoder and copies their contribution into a normal writable target.

## Legacy documents

To adopt a Configuration.Writable JSON or YAML file, read it with the simple document layout (`DocumentLayout.Simple`, the default): the codec recognizes inline `$version` (and the `Version` fallback), defaults a schema-annotated object or mapping without a version to version 1, and can attribute that version to the current Configlue model ID for historical dispatch. `$schema` is stripped as metadata. Empty or whitespace-only YAML is read as an empty sparse fragment. For BOM-encoded YAML, the codec detects the encoding automatically; when reading a nested section with an explicit non-UTF-8 encoding, pass it as `textEncoding` to both `YamlSectionResource` and the codec. Select a nested section first with `JsonSectionResource` or `YamlSectionResource`, then wrap it in `SerializedStateReader<TFragment>` and a `StateSource<TFragment>` with no writer.

```csharp
var oldFile = new FileResource("./old-settings.json");
var oldSection = new JsonSectionResource(
    oldFile,
    writer: null,
    sectionPath: "ApplicationSettings:Database",
    watcher: null);
var oldReader = new SerializedStateReader<AppSettings.Fragment>(
    oldSection,
    new JsonStateCodec<AppSettings.Fragment>(
        documentLayout: new DocumentLayoutOptions
        {
            ModelId = AppSettings.ConfiglueSchema.ModelId,
        }));
var oldSource = new StateSource<AppSettings.Fragment>("legacy", oldReader);
var currentSource = CreateCurrentSettingsSource(); // writable source using the normal Configlue codec

await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
    new StateSourceSet<AppSettings.Fragment>([oldSource, currentSource]),
    StateWriteRoute.To("current"));

// Copy only the old contribution and leave oldFile available for retry/recovery.
await options.MigrateSourceAsync("legacy", "current");
```

The file and section resource above remain application-owned. For a file with historical field shapes, pass a schema dispatcher to `SerializedStateReader<TFragment>` and register a matching legacy codec for each historical fragment.

## Rules for a safe adoption

* Add the legacy source as a read-only migration input and copy only its source ID to the writable target with `MigrateSourceAsync` or `MigrateSourcesToTargetsAsync`.
* Keep the original file until target verification succeeds; if migration fails, retry with the same source and target definitions.
* Remove the old file only through an explicit application decision.

## Next steps

* [Schema migration](./schema-migration.md) for version chains after adoption.
* [Storage migration](./storage-migration.md) for multi-target moves.
