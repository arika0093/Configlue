---
title: JSON Schema and testing
description: Export versioned schemas and test with in-memory doubles.
---

# JSON Schema and testing

## JSON Schema export

`JsonSchemaGenerator.Generate` and `Write` export versioned schemas from a model's generated `ConfiglueModelSchema`; pass a source-generated `IJsonTypeInfoResolver` for trimming and NativeAOT-friendly metadata. Supported DataAnnotations are mapped to schema constraints.

```csharp
var result = JsonSchemaGenerator.Generate(
    [SampleSetting.ConfiglueModelSchema],
    SampleSettingJsonContext.Default);
```

`Generate` builds the documents in memory; `Write` persists them. Check the result diagnostics (`CWSC001` reports frameworks without `System.Text.Json` schema-export support). Host the files wherever fits — a `main`-branch folder, a CDN, or release assets — and point editors at them.

Pass an absolute `schemaBaseUri` such as `https://example.com/schemas/` to set each document's root `$id` to that URI plus its versioned file name (for example, `https://example.com/schemas/AppSettings.v1.json`). The final slash is added when needed. The URI cannot contain a query or fragment; invalid values return diagnostic `CWSC012`. A non-null value also permits an optional `$schema` string property in the configuration model. `Write` still writes to the local output directory; publishing those files is a separate step.

## Testing

The testing helpers live in the separate `Configlue.Testing` package:

```shell
dotnet add package Configlue.Testing
```

* `InMemoryResource` — a resource reader/watcher/batch-writer double with a `WriteCount` probe. Compose it with `SerializedStateSource.FromResource` to test resolution, writes, and watchers without touching the file system.
* `InMemoryStateStore<T>` — a reader/writer/watcher double that can be seeded with `Set(value)`, forced to `SetNotFound()` / `SetUnavailable()`, and observed directly.

## Next steps

* [Backups and observability](./backups-and-observability.md).
* [NativeAOT](./native-aot.md).
