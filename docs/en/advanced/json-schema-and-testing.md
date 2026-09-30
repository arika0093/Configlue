---
title: JSON Schema and testing
description: Export versioned schemas and test with in-memory doubles.
---

## JSON Schema export

JSON Schema generation lives in the build-time `Configlue.JsonSchema.MSBuild` package. The application and the `Configlue` meta-package have no runtime schema-generation API and no `JsonSchema.Net` dependency. The package discovers `[ConfiglueModel]` types in the built assembly by reflection and writes schemas after `Build`.

```shell
dotnet add package Configlue.JsonSchema.MSBuild
```

Referencing the package enables generation without any application code. Set `ConfiglueGenerateSchemas` to `false` to opt out, and use the explicit `GenerateConfiglueSchemas` target for tooling or CI:

```shell
dotnet msbuild -t:GenerateConfiglueSchemas
```

For a guided walkthrough, see [Export JSON Schema](../getting-started/08-json-schema.md).

### Output and layout

The default output root is resolved in this order:

1. the explicit `ConfiglueSchemaOutputPath`,
2. `$(SolutionDir)/schemas` when available,
3. the nearest parent directory containing a `.sln` or `.slnx`, followed by `schemas`,
4. `$(MSBuildProjectDirectory)/schemas`.

Schemas are written once per project build even when the project multi-targets (`ConfiglueSchemaTargetFramework` selects the framework whose output is inspected, defaulting to the first), and unchanged files are not rewritten.

Pass an absolute `ConfiglueSchemaBaseUri` such as `https://example.com/schemas/` to set each generated schema's root `$id` to that URI plus its versioned file name (for example, `https://example.com/schemas/AppSettings.v1.json`). The final slash is added when needed; the URI cannot contain a query or fragment. By default, the generated schema describes the simple persisted document, with `$version` and optional model members at the root and no model ID. Set `ConfiglueSchemaDocumentLayout` to `Detailed` when you need the `$configlue`/`$value` envelope, and `ConfiglueSchemaVersionProperty` to change the version property name used by the simple layout. A base URI also adds an optional root `$schema` property to the exported schema.

To put a reference in files written by a JSON or YAML file source, set its `SchemaReferenceBaseUri`; the writer appends the versioned model-specific filename. JSON stores a root `$schema` member, while YAML stores a `yaml-language-server` directive comment. Section sources reject this option because their root document shape differs. The MSBuild package writes schema files to the local output directory; publishing those files is a separate step.

### Dependency policy

The schema tool pins its `JsonSchema.Net` generation stack to explicitly approved pre-OSMF versions. The references, the approved-version policy (`AllowedJsonSchemaNetVersion` and related properties), and `packages.lock.json` are validated independently, so an update that crosses the approved dependency boundary fails the ordinary build unless the policy is changed deliberately.

## Testing

The testing helpers live in the separate `Configlue.Testing` package:

```shell
dotnet add package Configlue.Testing
```

* `InMemoryResource` — a resource reader/watcher/batch-writer double with a `WriteCount` probe. Compose it with `SerializedStateSource.FromResource` to test resolution, writes, and watchers without touching the file system.
* `InMemoryStateSource<T>` — a reader/writer/watcher double that can be seeded with `Set(value)`, forced to `SetNotFound()` / `SetUnavailable()`, and observed directly.

## Next steps

* [Backups, logging, and diagnostics](./backups-and-observability.md).
* [NativeAOT](./native-aot.md).
