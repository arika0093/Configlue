---
title: Files and sections
description: JSON, YAML, and XML file sources plus nested section views.
---

# Files and sections

Provider packages add one-call source registrations to the shared `Sources` builder. JSON, YAML, and XML files share the same options shape.

```csharp
using Configlue.Provider.Json;
using Configlue.Provider.Yaml;

model.Sources(sources =>
{
    sources.FromJsonFile(new()
    {
        Id = "user-json",
        Path = "settings.json",
        SectionPath = "Application:User",
        Priority = 100,
    });
    sources.FromYamlFile(new()
    {
        Id = "defaults-yaml",
        Path = "defaults.yaml",
        Priority = 10,
        ReadOnly = true,
    });
});
model.WriteRoute = StateWriteRoute.To("user-json");
```

`FromXmlFile(new() { ... })` uses the same options for an XML file and optional element path. File helpers work in non-DI and DI contexts; a generated file resource belongs to the context and is disposed after its watcher stops, while directly supplied clients remain owned by the caller.

## Resolution rules

* Reads merge the present members from each source; higher `Priority` wins per member.
* File sources fall through when the file is missing; other read failures propagate.
* Mark a source `ReadOnly = true` to exclude it from write planning.

## Section resources

`JsonSectionResource` exposes a nested JSON path such as `App:Settings` as a separate resource and preserves its sibling values on writes. `XmlSectionResource` and `YamlSectionResource` provide the same nested-section view for XML elements and YAML mappings, including sibling preservation and whole-resource revision checks.

Disjoint section mounts that share one resource are combined into one physical write. For split files, use one `FileResource` per file and mount each source at its logical model path; each file is written independently. Section resources retain their physical `ResourceId` while keeping distinct logical source IDs and priorities.

Constraints: section edits require standard JSON or UTF-8 YAML. JSON with comments/trailing commas (JSONC) is not supported, and section writes reserialize the document — comment, whitespace, quoting, and scalar-style preservation is not guaranteed. YAML input with invalid UTF-8 fails instead of being replacement-decoded.

For YAML member naming, pass the naming policy and the generated fragment schema to the codec:

```csharp
new YamlStateCodec<SampleSetting.Fragment>(JsonNamingPolicy.CamelCase, SampleSetting.FragmentSchema)
```

## Next steps

* [Environment and command line](./environment-and-commandline.md).
* [Mount and project](../layering/mount-and-project.md) for composing section sources into nested models.
