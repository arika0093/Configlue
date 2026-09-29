---
title: Adopting Configuration.Writable
description: Read legacy inline-versioned files as migration inputs.
---

You can adopt Configlue while keeping files written by [Configuration.Writable](https://github.com/arika0093/Configuration.Writable). The legacy files stay untouched; Configlue reads them through an opt-in decoder and copies their contribution into a normal writable target.

## Legacy documents

To adopt a Configuration.Writable JSON or YAML file, read it with the simple document layout (`DocumentLayout.Simple`, the default): the codec recognizes inline `$version` (and the `Version` fallback), defaults a schema-annotated object or mapping without a version to version 1, and can attribute that version to the current Configlue model ID for historical dispatch. A `StateSchemaDispatcher<T>` also associates an inline version without a model ID with its target model. `$schema` is stripped as metadata. Empty or whitespace-only YAML is read as an empty sparse fragment. For BOM-encoded YAML, the codec detects the encoding automatically; when reading a nested section with an explicit non-UTF-8 encoding, pass it as `textEncoding` to both `YamlSectionResource` and the codec. Select a nested section first with `JsonSectionResource` or `YamlSectionResource`, then wrap it in `SerializedStateReader<TFragment>` and a `StateSource<TFragment>` with no writer.

```csharp
using Configlue.Codecs;
using Configlue.Migrations;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using Configlue.Provider.Json;

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

await using var context = ConfiglueApp.CreateContext(app =>
    app.Add<AppSettings>(model =>
    {
        model.Sources(sources =>
        {
            sources.Add(oldSource);
            sources.Add(currentSource);
        });
        model.WriteRoute = StateWriteRoute.To("current");
    }));

// Copy only the old contribution and leave oldFile available for retry/recovery.
await context.GetSources<AppSettings>().MigrateSourceAsync("legacy", "current");
```

The file and section resource above remain application-owned. For a file with historical field shapes, pass a schema dispatcher to `SerializedStateReader<TFragment>` and register a matching legacy codec for each historical fragment.

## Rules for a safe adoption

* Add the legacy source as a read-only migration input and copy only its source ID to the writable target with `MigrateSourceAsync` or `MigrateSourcesToTargetsAsync`.
* Keep the original file until target verification succeeds; if migration fails, retry with the same source and target definitions.
* Remove the old file only through an explicit application decision.

## Migrating the application API

After adopting the file, replace the application's registration and read/write calls. Register sources and their write destinations explicitly with `conf.Add<TModel>(...)`, using a provider that matches the existing file format.

| Configuration.Writable | Configlue |
| --- | --- |
| `[OptionsModel]` | `[ConfiglueModel]`. Mark the class `partial` and use its generated `Patch` for sparse saves. |
| `WritableOptions.Initialize(...)` | `ConfiglueApp.CreateContext(...)`. For a shared default context, use `ConfiglueApp.Initialize(...)` and `GetState<T>()`. |
| `WritableOptions.GetState<T>()` | `context.GetState<T>()` or `ConfiglueApp.GetState<T>()`. |
| `CurrentValue` | `await state.GetValueAsync()`. Configlue's core API is asynchronous. DI applications that need synchronous `IOptions<T>` adapters can opt in to `Configlue.Extensions.MSOptions`. |
| `SaveAsync(value => ...)` | `await state.SaveAsync(patch => ...)` to save only changed members. Use `context.GetEditSessions<T>().OpenEditSessionAsync()` when editing the resolved model as a whole. |
| `OnChange(...)` / `OnReloadFailed(...)` | Use `state.OnChange(...)` and `context.GetDiagnostics<T>().OnReloadFailed(...)`. Dispose each returned subscription when it is no longer needed. |
| `InstanceName` / named options | Set `StateName` at registration for fixed names. Use `EnableDynamicStates` and `GetStateRegistry<T>()` to add or remove names at runtime. Use `EnableProfiles(...)` when the profile catalog must persist. |
| `ConfigurationInfo` | Use `context.GetDiagnostics<T>().GetDiagnostics()` for source topology and write routes, and `await state.GetDetailsAsync()` for values and their source provenance. |
| `AddWritableOptions(...)` | `services.AddConfiglue(...)`. Add `AddConfiglueMicrosoftOptions<T>()` when `IOptions<T>` adapters are also needed. |

Example using an independent non-DI context:

```csharp
await using var context = ConfiglueApp.CreateContext(config =>
{
    config.Add<UserSettings>(model =>
    {
        model.UseDefaultJsonFile();
    });
});

var state = context.GetState<UserSettings>();
var value = await state.GetValueAsync();
using var subscription = state.OnChange(updated => Console.WriteLine(updated.Name));
await state.SaveAsync(patch => patch.Name = "new name");
```

`CreateContext` manages the lifetime of its context and the watchers it starts. Sources and resources supplied by the application remain application-owned. See [Application setup](../basic-usage/app-setup.md) for fixed named states and DI registration, and [Dynamic states](../profiles/dynamic-states.md) and [Profiles](../profiles/profiles.md) for the difference between runtime names and persisted profiles.

## Next steps

* [Schema migration](./schema-migration.md) for version chains after adoption.
* [Storage migration](./storage-migration.md) for multi-target moves.
