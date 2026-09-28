---
title: Files, formats, and sections
description: JSON, YAML, and XML formats and file sources plus nested section views.
---

# Files, formats, and sections

Provider packages add one-call source registrations to the shared `Sources` builder. JSON, YAML, and XML files share the same options shape.

```csharp
using Configlue.Provider.Json;
using Configlue.Provider.Yaml;

model.Sources(sources =>
{
    sources.FromJsonFile(new()
    {
        Path = "settings.json",
        SectionPath = "Application:User",
        Priority = 100,
    });
    sources.FromYamlFile(new()
    {
        Path = "defaults.yaml",
        Priority = 10,
        ReadOnly = true,
    });
});
```

File sources receive a stable opaque identity from their normalized resource and section. Set `Id` only when an advanced integration needs a stable diagnostic or migration identifier.

`FromXmlFile(new() { ... })` uses the same options for an XML file and optional element path. File helpers work in non-DI and DI contexts; a generated file resource belongs to the context and is disposed after its watcher stops, while directly supplied clients remain owned by the caller.

## Resolution rules

* Reads merge the present members from each source; higher `Priority` wins per member.
* File sources fall through when the file is missing; other read failures propagate.
* Mark a source `ReadOnly = true` to exclude it from write planning.

## Section resources

`JsonSectionResource` exposes a nested JSON path such as `App:Settings` as a separate resource and preserves its sibling values on writes. `XmlSectionResource` and `YamlSectionResource` provide the same nested-section view for XML elements and YAML mappings, including sibling preservation and whole-resource revision checks.

Disjoint section mounts that share one resource are combined into one physical write. For split files, use one `FileResource` per file and mount each source at its logical model path; each file is written independently. Section resources retain their physical `ResourceId` while keeping distinct logical source IDs and priorities.

JSON file sources accept both `.json` and `.jsonc` content, including comments and trailing commas. JSON and YAML file writes update the affected values in place and preserve unrelated comments, whitespace, quoting, and scalar styles. If a write removes a modeled member, that member is removed while unknown properties remain. YAML input with invalid UTF-8 fails instead of being replacement-decoded. This structural editing is provided by the JSON and YAML file registrations; other codecs keep their existing replacement behavior.

For YAML member naming, pass the naming policy and the generated fragment schema to the codec:

```csharp
new YamlStateCodec<SampleSetting.Fragment>(JsonNamingPolicy.CamelCase, SampleSetting.FragmentSchema)
```

## Next steps

* [Environment and command line](./environment-and-commandline.md).
* [Mount and project](../layering/mount-and-project.md) for composing section sources into nested models.
