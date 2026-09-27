---
title: JSON Schema and testing
description: Export versioned schemas and test with in-memory doubles.
---

# JSON Schema and testing

## JSON Schema export

`JsonSchemaGenerator.Generate` and `Write` export versioned schemas from a model's generated `ConfiglueModelSchema`; pass a source-generated `IJsonTypeInfoResolver` for trimming and NativeAOT-friendly metadata. Supported DataAnnotations are mapped to schema constraints.

```csharp
var result = JsonSchemaGenerator.Generate(
    [SampleSetting.ConfiglueSchema],
    SampleSettingJsonContext.Default);
```

`Generate` builds the documents in memory; `Write` persists them. Check the result diagnostics (`CWSC001` reports frameworks without `System.Text.Json` schema-export support). Host the files wherever fits — a `main`-branch folder, a CDN, or release assets — and point editors at them.

To restore the legacy `--cw-generate-json-schema <directory>` startup path, collect registrations in a `ConfiglueBuilder` and call `TryWriteFromCommandLine` before building the application. The helper returns a result and leaves process exit behavior to the host:

```csharp
var config = new ConfiglueBuilder();
config.Add<AppSettings>(_ => { /* register sources and options */ });

if (JsonSchemaGenerator.TryWriteFromCommandLine(
    args,
    config.ModelSchemas,
    AppJsonContext.Default,
    out var schemaResult))
{
    var generation = schemaResult!;
    foreach (var diagnostic in generation.Diagnostics)
        Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");

    return generation.Succeeded ? 0 : 1;
}

using var context = config.CreateContext();
```

The output directory is required after the option; `--cw-generate-json-schema=schemas` is also accepted. For DI, pass the same collected builder to `services.AddConfiglueBuilder(config)` after the command-line check.

Pass an absolute `schemaBaseUri` such as `https://example.com/schemas/` to set each generated schema's root `$id` to that URI plus its versioned file name (for example, `https://example.com/schemas/AppSettings.v1.json`). The final slash is added when needed. The URI cannot contain a query or fragment; invalid values return diagnostic `CWSC012`. By default, the generated schema describes the simple persisted document, with `$version` and sparse model members at the root and no model ID. Select `DocumentLayout.Detailed` when you need the `$configlue`/`$value` envelope. A non-null value adds an optional root `$schema` property to the exported schema. To put a reference in files written by a JSON or YAML file source, set its `SchemaReferenceBaseUri`; the writer appends the versioned model-specific filename. JSON stores a root `$schema` member, while YAML stores a `yaml-language-server` directive comment. Section sources reject this option because their root document shape differs. `Write` still writes generated schema files to the local output directory; publishing those files is a separate step.

## Testing

The testing helpers live in the separate `Configlue.Testing` package:

```shell
dotnet add package Configlue.Testing
```

* `InMemoryResource` — a resource reader/watcher/batch-writer double with a `WriteCount` probe. Compose it with `SerializedStateSource.FromResource` to test resolution, writes, and watchers without touching the file system.
* `InMemoryStateStore<T>` — a reader/writer/watcher double that can be seeded with `Set(value)`, forced to `SetNotFound()` / `SetUnavailable()`, and observed directly.

## Next steps

* [Backups, logging, and diagnostics](./backups-and-observability.md).
* [NativeAOT](./native-aot.md).
