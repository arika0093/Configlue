---
title: "STEP 7: Export JSON Schema"
description: Generate schemas from models for editor completion and CI checks.
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 7: Export JSON Schema

Settings typos should surface before runtime. Configlue exports JSON Schema from a model's generated `ConfiglueModelSchema` — for editor completion and CI validation.

Schema generation is a build-time tool and does not depend on whether the application uses DI.

## Write the schema

```csharp
using System.Text.Json.Serialization;
using Configlue.Provider.Json;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppSettings.Fragment))]
internal partial class AppJsonContext : JsonSerializerContext;
```

The call is the same for applications with or without DI:

<Tabs syncKey="di">
<TabItem label="Without DI">

```csharp
var result = JsonSchemaGenerator.Write<AppSettings, AppSettings.Fragment>(
    "schemas",
    AppJsonContext.Default);
Console.WriteLine($"Wrote: {string.Join(", ", result.WrittenFiles)}");
```

</TabItem>
<TabItem label="With DI">

```csharp
var result = JsonSchemaGenerator.Write<AppSettings, AppSettings.Fragment>(
    "schemas",
    AppJsonContext.Default);
Console.WriteLine($"Wrote: {string.Join(", ", result.WrittenFiles)}");
```

</TabItem>
</Tabs>

`Write` creates files in a local output directory. Publish that directory to GitHub Pages or another host separately. Passing an absolute `schemaBaseUri`, such as `https://example.com/schemas/`, sets each generated document's `$id` to the base URI plus its versioned file name and includes an optional root `$schema` property in the generated configuration schema. It does not change the output directory. Output is versioned, so re-export when the model `Version` rises. Supported DataAnnotations map to schema constraints. The exported schema describes Configlue's persisted envelope: `$configlue` carries the model ID and version, and `$value` carries the sparse settings fragment.

## Use it in editors and CI

To add a versioned reference when a JSON file source writes settings, configure its `SchemaReferenceBaseUri`. The writer appends the model-specific versioned schema filename. For example:

```csharp
sources.FromJsonFile(new()
{
    Id = "settings",
    Path = "settings.json",
    SchemaReferenceBaseUri = "./schemas/"
});
```

The saved file then has the schema reference at the root, alongside the same envelope the generated schema describes:

```json
{
  "$schema": "./schemas/tutorial.settings.v1.json",
  "$configlue": { "id": "tutorial.settings", "version": 1 },
  "$value": {
    "Server": { "Host": "localhost", "Port": 8080 }
  }
}
```

For YAML writers, `SchemaReferenceBaseUri` adds a `yaml-language-server` schema directive comment at the top of the file. A file source using `SectionPath` cannot add a root reference because its schema would describe the wrong document shape. `Write` remains a local file writer; publish generated schemas separately. Editors like VS Code gain completion and hover docs; CI can validate against the schema.

Next: [STEP 8: Version and migrate](./08-migration.md). Decide how model changes treat old files.
