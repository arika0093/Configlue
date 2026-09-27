# Configlue

Typed configuration assembled from independent state sources.

Configlue is a source-generator-first .NET library for reading, resolving, editing, and persisting typed configuration. Each source contributes only the fields it owns, so values can be layered — JSON files, environment variables, command-line options, HTTP resources — without replacing an entire settings object.

Browse the [Configlue documentation site](https://arika0093.github.io/Configlue/) for setup guides, provider information, and API concepts.

## Features

* Read, resolve, edit, and persist typed settings with [sparse generated fragments](#define-a-model).
* [Layer independent sources](#configuration-method) with priority-based resolution: JSON, XML, YAML, environment variables, command line, HTTP, ZIP entries, and custom sources.
* [Write back sparsely](#save-and-edit): route edits to a chosen source, split one edit across sources, or patch a single source contribution.
* [Built-in](#file-sources) atomic file writing with backup generations and restore.
* [Automatic detection](#change-detection) of external changes with debounced notifications.
* Simple API for applications both [without](#simple-application-without-di) and [with](#host-application-with-di) DI. Microsoft `IOptions<T>` adapters are available from the optional `Configlue.Extensions.MSOptions` package.
* [Named profiles](#profiles) with a persisted catalog, and [runtime dynamic options](#dynamic-options) for multi-document scenarios.
* [Schema and storage migration](#migration): versioned models, source-to-source copies, and adoption of existing `Configuration.Writable` files.
* [JSON Schema export](#json-schema-support) from generated models.
* Works with [NativeAOT](#support-nativeaot) environments.

## Quick Start

Save the code below to `example.cs` and run it with `dotnet run example.cs` (requires .NET 10 or later).

```csharp
#!/usr/bin/env dotnet
#:package Configlue@*

using Configlue;
using Configlue.Provider.Json;

// 1. Declare the settings model. The generator creates Fragment/Patch support.
[ConfiglueModel("SampleSetting", Version = 1)]
public partial class SampleSetting
{
    public string Name { get; set; } = "default name";
}

// 2. Initialize once (at application startup).
await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<SampleSetting>(model =>
    {
        model.UseDefaultJsonFile();
    });
});

// 3. Read and write through the options instance.
var options = context.GetOptions<SampleSetting>();
var current = await options.GetValueAsync();
Console.WriteLine($"Current Name: {current.Name}");

await options.SaveAsync(patch => patch.Name = "Alice");
Console.WriteLine($"Saved. New Name: {(await options.GetValueAsync()).Name}");
```

The output will be as follows:

```log
Current Name: default name
Saved. New Name: Alice
```

`UseDefaultJsonFile()` stores settings in `usersettings.json` beside the application executable. Use `UseJsonFile(path)` to choose a path. Editing the file externally notifies the application (see [Change Detection](#change-detection)).

## Usage

### Setup

Install `Configlue` from NuGet. This meta-package brings in the abstraction contracts, the core runtime, DI integration, the JSON provider, HTTP resources, the environment source, and the source generator.

```bash
dotnet add package Configlue
```

Add format or source packages only when you need them (`Configlue.Provider.Yaml`, `Configlue.Provider.Xml`, `Configlue.Source.CommandLine`, `Configlue.Source.Common`, `Configlue.Resource.Zip`, `Configlue.Testing`, …). See [Packages](#packages) for the full list.

Requires the .NET 10 SDK:

```sh
dotnet build Configlue.slnx
dotnet test --solution Configlue.slnx --configuration Release
```

### Define a model

Add `[ConfiglueModel]` to the class and mark it as `partial`. This generates sparse `Fragment`/`Patch` support used for resolution, diffs, and migration.

```csharp
using Configlue;

[ConfiglueModel("UserSetting", Version = 1)]
public partial class UserSetting
{
    // Default values are used when no source provides a member.
    public string Name { get; set; } = "default name";
    public int Age { get; set; } = 20;
}
```

> [!TIP]
> Be sure to add `partial`. Nested models need no attribute; only the root model that represents a configuration unit declares `[ConfiglueModel]`.

### Simple Application (Without DI)

If you are not using DI (for example, in WinForms, WPF, or console apps), use `ConfiglueApp` as the starting point. `CreateContext` creates an independent lifetime-managed context; `Initialize`/`GetOptions` share a process-wide default context instead.

```csharp
using Configlue;
using Configlue.Provider.Json;

// initialize once (at application startup)
await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<SampleSetting>(model =>
    {
        model.UseDefaultJsonFile();
    });
});

// get the options instance with the specified setting class
var options = context.GetOptions<SampleSetting>();

// get the current value
var setting = await options.GetValueAsync();
Console.WriteLine($">> Name: {setting.Name}");

// and save to storage (sparse edit: untouched members are preserved)
await options.SaveAsync(patch => patch.Name = "new name");
```

After `using Configlue;`, `ConfiglueApp.Initialize(...)` and `ConfiglueApp.GetOptions<T>()` provide the process-wide default context; call `await ConfiglueApp.ShutdownAsync()` to dispose it. A `ConfiglueContext` owns the options and watcher tasks it creates. Source, reader, writer, and resource instances supplied by the application remain caller-owned.

> [!IMPORTANT]
> Always register the type with `CreateContext`/`Initialize` before calling `GetOptions`.

### Host Application (With DI)

If you are using DI (for example, in ASP.NET Core, Blazor, or Worker Service), register Configlue in the container. Use the same model and source definitions as the non-DI setup; the service provider owns the context.

```csharp
// Program.cs
builder.Services.AddConfiglue(conf =>
{
    conf.Add<UserSetting>(model =>
    {
        model.Sources(sources => sources.FromJsonFile(new()
        {
            Id = "settings",
            Path = "usersettings.json",
        }));
        model.WriteRoute = StateWriteRoute.To("settings");
    });
});
```

Then inject `IReadOnlyOptions<T>` or `IWritableOptions<T>` to read and write settings.

```csharp
// read config in your class
// install Configlue.Extensions.MSOptions and call AddConfiglueMicrosoftOptions<UserSetting>() to opt in to IOptions adapters
public class ConfigReadService(IReadOnlyOptions<UserSetting> options)
{
    public async Task PrintAsync()
    {
        var setting = await options.GetValueAsync();
        Console.WriteLine($">> Name: {setting.Name}");
    }
}

// read and write config in your class
public class ConfigReadWriteService(IWritableOptions<UserSetting> options)
{
    public async Task UpdateAsync()
    {
        await options.SaveAsync(patch => patch.Name = "new name");
    }
}
```

### ASP.NET Core (With DI)

By mounting a [section resource](#section-resources), you can dynamically update an existing configuration file such as `appsettings.json` while leaving sibling values untouched.

```csharp
var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddConfiglue(conf =>
{
    conf.Add<UserSetting>(model =>
    {
        model.Sources(sources => sources.FromJsonFile(new()
        {
            Id = "settings",
            Path = "appsettings.json",
            SectionPath = "MySetting",
        }));
        model.WriteRoute = StateWriteRoute.To("settings");
    });
});

// In this case, the settings are saved under the `MySetting` section in `appsettings.json`.
```

Reading and writing settings works the same way as in [Host Application](#host-application-with-di).

## Customization

* [Configuration Method](#configuration-method)
* [File Sources](#file-sources)
* [Section Resources](#section-resources)
* [Common Layered Sources](#common-layered-sources)
* [Environment Variables](#environment-variables)
* [Command Line](#command-line)
* [HTTP and ZIP Resources](#http-and-zip-resources)
* [Fallback Sources](#fallback-sources)
* [Save and Edit](#save-and-edit)
* [Write Routing](#write-routing)
* [Change Detection](#change-detection)
* [Validation](#validation)
* [Profiles](#profiles)
* [Dynamic Options](#dynamic-options)
* [Diagnostics and Logging](#diagnostics-and-logging)

### Configuration Method

You can register multiple types in the same block with per-model configuration. The one-type-argument `conf.Add<T>(...)` form works for both DI and non-DI setups.

```csharp
// Without DI
await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<SampleSetting>(model =>
    {
        // model-specific configuration for SampleSetting
    });
});

// With DI
builder.Services.AddConfiglue(conf =>
{
    conf.Add<UserSetting>(model =>
    {
        // model-specific configuration for UserSetting
    });
});
```

Under the following, `builder.Services.AddConfiglue(...)` can be read as `ConfiglueApp.CreateContext(...)` and vice versa.

### File Sources

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

Reads merge the present members from each source; higher `Priority` wins for members present in more than one source. File sources fall through when the file is missing; other read failures propagate. `FromXmlFile(new() { ... })` uses the same options for an XML file and optional element path.

File resources keep one atomic `.bak` generation by default; `FileResourceOptions` can retain more generations in a chosen directory, and `RestoreLatestBackupAsync` restores the newest one explicitly. See [Backups](https://arika0093.github.io/Configlue/en/advanced/backups-and-observability/) on the documentation site.

### Section Resources

`JsonSectionResource` exposes a nested JSON path such as `App:Settings` as a separate resource and preserves its sibling values on writes. `XmlSectionResource` and `YamlSectionResource` provide the same nested-section view for XML elements and YAML mappings, including whole-resource revision checks.

```csharp
model.Sources(sources =>
{
    sources.FromJsonFile(new() { Id = "settings", Path = "settings.json", Priority = 10 });
    sources.AddMounted<AppSettings, AppSettings.Fragment, PolicySettings, PolicySettings.Fragment>(
        policyHttpSource,
        model => model.Policy,
        root => root.Policy.Value!);
});
```

Section edits require standard JSON or UTF-8 YAML. JSON with comments/trailing commas (JSONC) is not supported, and section writes reserialize the document: comment, whitespace, quoting, and scalar-style preservation is not guaranteed.

Disjoint section mounts that share one resource are combined into one physical write. For split files, use one `FileResource` per file and mount each source at its logical model path.

### Common Layered Sources

The optional `Configlue.Source.Common` package composes global, local, explicitly selected, and environment/command-line layers for either `CreateContext` or `AddConfiglue`:

```csharp
using Configlue.Source.Common;

config.Add<AppSettings>(model => model.UseCommonSources(new CommonSourceOptions
{
    ApplicationId = "ExampleApp",
    GlobalFileName = "settings.json",
    SpecificFilePath = selectedPath,
    EnvironmentPrefix = "EXAMPLE",
    CommandLineParseResult = parseResult,
    ConfigureCommandLineMappings = mappings => mappings.Map<AppSettings, int>(portOption, settings => settings.Server!.Port),
    WriteLayer = CommonSourceWriteLayer.Global,
}));
```

`UseCommonSources` expands to these stable logical sources:

| Source ID | Priority | Included when | Writable |
| --- | ---: | --- | --- |
| `common.global` | 100 | `EnableGlobalFile` | Only when selected by `WriteLayer` |
| `common.local` | 200 | `EnableLocalFile` | Only when selected by `WriteLayer` |
| `common.specific` | 300 | `EnableSpecificFile` and `SpecificFilePath` is set | Only when selected by `WriteLayer` |
| `common.environment` | 400 | `EnableEnvironment` and `EnvironmentPrefix` is set | No |
| `common.commandLine` | 500 | `EnableCommandLine` and `CommandLineParseResult` is set | No |

`ConfiglueStandardPaths.GetStandardSaveDirectory(applicationId)` returns the platform-standard per-user configuration directory plus the application identifier. Set the `Enable*` switches to omit layers, or set `LocalFilePath`, `SpecificFilePath`, and `WriteLayer` to change their locations and destination.

### Environment Variables

`EnvironmentStateSource.FromEnvironment<TModel, TFragment>(id, prefix)` creates a read-only sparse source from process environment variables such as `APP__DATABASE__HOST`. Double underscores separate nested model members; member names are matched case-insensitively. A property annotated with `[ConfiglueEnvironment("ENV_NAME")]` reads that mapped variable name instead, including for nested model properties. The reader recalculates a content revision on each read; process environment variables do not provide a watcher. Through the one-argument facade, the same source registers as `sources.FromEnvironment(new() { Id = "environment", Prefix = "APP", Priority = 400 })`, with optional `EnvironmentVariables` and `ValueParser` overrides for tests and custom hosts.

### Command Line

`Configlue.Source.CommandLine` accepts the application's existing `System.CommandLine` parse result and explicit symbol-to-path mappings. Parser defaults do not become overrides unless the symbol was explicitly supplied; a parse result with errors fails the source read. Map root and selected subcommand symbols explicitly, and create a new source/context when command-line input changes.

```csharp
using Configlue.Source.CommandLine;
using System.CommandLine;

model.Sources(sources => sources.FromCommandLine(new CommandLineSourceOptions
{
    Id = "command-line",
    ParseResult = parseResult,
    Priority = 500,
},
mappings =>
{
    mappings.Map<AppSettings, int>(portOption, settings => settings.Server!.Port);
    mappings.Map<AppSettings, bool>(verboseOption, settings => settings.Diagnostics!.Verbose);
}));
```

### HTTP and ZIP Resources

`HttpResourceReader` reads from `{root}/get` and can be composed with any state codec. Pass a direct `HttpClient` outside DI, or set `ClientFactory = provider => provider!.GetRequiredService<IHttpClientFactory>().CreateClient("settings")` in DI. HTTP writes are disabled unless `Writable = true`; provide a codec such as `new JsonStateCodec()` explicitly. Call `CreateWriter()` and pass the result as `writer:` to `SerializedStateSource.FromResource` only when the endpoint supports updates; HTTP requests use ETags for conditional writes and polling. The optional `Configlue.Resource.Http.AspNetCore` package maps the same protocol over user-provided resource handlers. See [HTTP resources](https://arika0093.github.io/Configlue/en/sources/http-and-zip/) on the documentation site.

Host applications can register named clients with the standard `AddHttpClient` APIs and pass them to facade sources through `FromHttpClientFactory`. For JSON endpoints, `FromJsonHttp` and `FromJsonHttpClientFactory` create the JSON codec for you; set `Writable = true` only when the endpoint supports updates. These sources are read-only by default. The source resolves its client when the Configlue context is created; `IHttpClientFactory` manages the underlying handlers, and the source does not dispose the returned client. Use `FromHttp` when an endpoint uses a codec other than JSON.

`ZipEntryResource` exposes one archive entry as a logical resource while retaining the archive's physical identity and revision. Disjoint entry updates can share one batched archive write, and untouched entries remain intact.

### Fallback Sources

Use `FallbackStateSource<TFragment>` to group serialized representations of the same logical state, such as a canonical JSON file and a legacy YAML file. It reads the first successful candidate by priority and exposes that candidate as one source, so values from separate formats are never overlaid. By default, writes go to the active writable candidate; set `writeSourceId` to route edits to a fixed candidate such as the canonical file.

### Save and Edit

Use the generated `SaveAsync(patch => ...)` API for sparse edits. It changes only members specified by the Patch and leaves untouched source contributions alone. Use `OpenEditSessionAsync` for edits based on the resolved model and `Source(...).ReplaceAsync(patch => ...)` to withdraw unspecified members from one source.

```csharp
// Save a sparse patch to the configured write source.
await options.SaveAsync(patch => patch.SomeSetting = newValue);

// Edit several values together; the session is in-memory until SaveAsync.
using var edit = await options.OpenEditSessionAsync();
edit.Value.SomeSetting = newValue;
await edit.CommitAsync();

// Patch a single member. Unset removes only the write source's contribution.
var patch = new AppSettings.Patch();
patch.SomeSetting = newValue;
await options.SaveAsync(patch);
```

Configure sessions compare the full source revision vector immediately before saving and fail with `StateConflictException` if any participating source changed. Generated `TModel.Patch` values can also be sent through `ApplyPatchesAsync` with `StateSourcePatch` entries for an explicit source-local multi-write; disjoint section updates sharing a `ResourceId` persist with one physical write.

Use `IConfiglueOptions<T>.ExplainAsync("Database.Host")` to inspect the effective value and the present source contributions from highest to lowest priority.

### Write Routing

Set `ConfiglueModelBuilder<T>.WritePlan` to declare default owners for paths or subtrees; for example, route `Database` to a writable user overlay while leaving unrelated values in lower-priority sources. The most specific path wins, and paths without an owner use `WriteRoute` (or the highest-priority writable source). A per-operation `StateWritePlan` replaces registration routes for matching paths and can split nested model changes across source fragments.

```csharp
// Route selected model paths to different writable sources for one edit.
var writePlan = new StateWritePlan(new Dictionary<string, string>
{
    ["Database"] = "database-settings",
    ["Database.Password"] = "secrets",
});
using var routedEdit = await options.OpenEditSessionAsync(writePlan);
routedEdit.Value.Database!.Password = "updated";
var writeResult = await routedEdit.CommitAsync();
```

Plans validate paths and targets before editing, then verify the fully resolved model and all source revisions before writing. A read-only contribution that shadows the requested value causes a `StateConflictException`. Unchanged fields retain their existing sparse state. `Append` and `SetUnion` edits are rebased onto each target source's collection segment; edits hidden by a higher-priority source fail explicitly instead of being silently replaced. Customize built-in merging per member with `[ConfiglueMerge(MergeMode.Append)]` (or `Deep`, `Replace`, `SetUnion`).

Merge modes apply when a higher-priority fragment contains the member. Missing leaves the accumulated lower-priority value unchanged; present `null` replaces it. `Deep` recursively combines two present, non-null nested model fragments. `Append` concatenates ordered collections low-to-high and keeps duplicates; the generator rejects set-typed members because sets cannot preserve duplicates or sequence order. `SetUnion` uses the same low-to-high order and keeps the first value under default equality; arrays and lists preserve that order, while `HashSet<T>` enumeration order is unspecified.

For an application-specific merge algebra, use `[ConfiglueMerge(typeof(MyStrategy))]`. The strategy derives from `ConfiglueMergeStrategy<TMember>` and implements presence-aware `Merge`, semantic `AreEqual`, concurrent `TryRebase`, source-local `TryPlanSourceContribution`, and collection `ExplainElements`. The planner receives contributions from low to high priority and returns a sparse contribution or a reason to reject the edit. A single strategy instance is shared by the generated model, so implementations must be stateless and thread-safe. This connects resolution, generated diffs, rebasing, write planning, and element provenance to the same strategy.

```csharp
[ConfiglueMerge(typeof(FeatureMergeStrategy))]
public IReadOnlyList<string> Features { get; init; } = [];
```

### Change Detection

Change notifications are debounced by 300ms by default; pass `onChangeDebounce: TimeSpan.Zero` to a registration to disable it.

```csharp
using var changeSubscription = options.OnChange(updated =>
    Console.WriteLine($">> Settings changed: {updated.Name}"));
```

File, HTTP (polling), and custom watcher sources push updates through the same `OnChange` callback. `IOptionsMonitor<T>.OnChange` works in DI as well.

### Validation

DataAnnotations validation runs on save by default. Pass `validateDataAnnotations: false` to disable it. Use `AddConfiglueValidator<T>(IValidateOptions<T>)` for a Microsoft options validator; reflection-based DataAnnotations validation is skipped automatically when dynamic code is unavailable.

```csharp
builder.Services.AddConfiglue(conf =>
{
    conf.Add<UserSetting>(model =>
    {
        // ...sources...
    });
});
```

Microsoft options adapters are opt-in. Install `Configlue.Extensions.MSOptions` and call `services.AddConfiglueMicrosoftOptions<UserSetting>()` after registering Configlue options to add `IOptions<T>`, scoped `IOptionsSnapshot<T>`, and `IOptionsMonitor<T>` for a class model. Their synchronous `Value` and `Get` calls read Configlue state synchronously; use `ReadAsync` or `GetValueAsync` in asynchronous application flows.

### Profiles

Persistent named profiles use a separate writable source for their catalog. Each profile stores its values through the profile source factory, which receives the profile name — for example, separate files per profile.

```csharp
model.EnableProfiles(profileCatalogSource, defaultProfileName: "default");
model.SourcesForOptions((profileName, sources) =>
    sources.FromJsonFile(new()
    {
        Id = "profile-state",
        Path = Path.Combine(profileDirectory, profileName + ".json"),
    }));

var profiles = context.GetProfiledOptions<AppSettings>();
await profiles.CreateProfileAsync("work", copyFrom: "default");
await profiles.SetActiveProfileAsync("work");
var active = await profiles.GetActiveValueAsync();
await profiles.RemoveProfileAsync("work");
```

The profile catalog source must be writable and remains caller-owned. For DI, use the same `EnableProfiles` and `SourcesForOptions` calls inside `AddConfiglue(...)`, then resolve `IConfiglueProfiledOptions<AppSettings>` from the provider. Removing a profile removes it from the catalog and runtime; its backing state is retained.

### Dynamic Options

A model can opt in to dynamic named options with `model.EnableDynamicOptions = true`. The context exposes `GetOptionsRegistry<TModel>()`; `TryAdd(name)` creates the same source/model configuration under that `OptionsName`, and `TryRemoveAsync(name)` stops its watcher and disposes helper-created resources before returning.

```csharp
config.Add<AppSettings>(model =>
{
    model.EnableDynamicOptions = true;
    model.Sources(sources => sources.FromJsonFile(new()
    {
        Id = "tenant-settings",
        Path = "settings.json",
    }));
});

var registry = context.GetOptionsRegistry<AppSettings>();
registry.TryAdd("tenant-a");
var tenantOptions = context.GetOptions<AppSettings>("tenant-a");
await registry.TryRemoveAsync("tenant-a");
```

In DI, `IOptionsMonitor<AppSettings>.Get("tenant-a")` follows additions and removals through the registry. Dynamic named options are runtime-only; persisted profile catalogs are available separately through `EnableProfiles`.

The source set is fixed for each options runtime. To replace a runtime's source set, build a replacement context from the new definitions and coordinate the handoff in the application. The storage-migration API can retire selected sources after verified migration.

### Diagnostics and Logging

`IConfiglueOptions<T>.GetDiagnostics()` returns an immutable snapshot of the configured source topology for that options runtime, including source ID, priority, fallback policy, read/write/watch capabilities, physical origin, resource identity, and retired status. It also reports the default and property-path write routes; `GetWriteSourceId("Database.Endpoint")` resolves a registration-level route. Per-operation write plans are specific to that operation and are not included. Use `ReadAsync` for the latest read result and revisions, `ExplainAsync(path)` for effective values and their contributing sources, and write results for completed writes. No configuration values are written to logs.

```csharp
var diagnostics = options.GetDiagnostics();
var defaultWriteSource = diagnostics.GetWriteSourceId();
var endpointWriteSource = diagnostics.GetWriteSourceId("Database.Endpoint");
foreach (var source in diagnostics.Sources)
{
    Console.WriteLine(
        $"{source.Id}: priority={source.Priority}, active={source.IsActive}, " +
        $"read={source.CanRead}, write={source.CanWrite}, watch={source.CanWatch}, " +
        $"origin={source.PhysicalOrigin}, resource={source.ResourceId}");
}
```

Facade runtimes use `ILoggerFactory` from DI when one is registered. Non-DI callers can set `Logger` on `ConfiglueModelBuilder<TModel>`, and callers constructing `ConfiglueOptions<TModel, TFragment>` directly can pass its optional `logger` argument. Logging is optional; source read decisions, watcher failures, writes, migration outcomes, and revision conflicts use structured metadata such as model, options name, source ID, physical origin, and resource ID.

## Migration

Configuration is meant to evolve. Configlue migrates both schemas (model versions) and storage (source locations and formats).

### Schema versions

When a change is incompatible, increment the version, keep the old shape under a new name, and declare it as a previous version. Then implement the migration method; the interface is provided automatically.

```csharp
// Version 2 (new)
[ConfiglueModel("UserSetting", Version = 2)]
public partial class UserSetting
{
    public string Name { get; set; } = "default name";
}

// Version 1 (old)
[ConfiglueModel("UserSetting", Version = 1)]
public partial class UserSettingV1
{
    public string FirstName { get; set; } = "first";
    public string LastName { get; set; } = "last";
}

[ConfigluePreviousVersion(typeof(UserSettingV1))]
public partial class UserSetting
{
    public UserSetting Migrate(UserSettingV1 source) => new()
    {
        Name = $"{source.FirstName} {source.LastName}",
    };
}
```

Register `IStateSchemaMigration<TFragment>` implementations as services to migrate older fragments with the same generated shape. Generated members keep presence explicit: a `Fragment` exposes each member as `Optional<T>`, and `Fragment.FromPrevious` copies same-name, type-compatible members across declared versions. For a renamed member, assign explicitly with `builder.NewName.CopyFrom(previous.OldName)`.

### Storage migration

`IConfiglueOptions<T>.MigrateSourceAsync(sourceId, targetId)` copies one source contribution, applies its schema migration chain, and writes it to a selected destination. `MigrateSourcesToTargetsAsync(sourceIds, targetProjections)` merges selected contributions, revision-checks and verifies each target, and can retire the old sources after verification. Pass `retireSources: true` to remove the selected sources from that options instance after every target verifies and only when the effective model stays the same. Multi-target writes are not atomic; on retry, every target is rechecked against the current source contribution, and writes are skipped only for targets that already match. If a later target fails, rerun the migration to resume. Retired sources are removed from the running topology only — update the application's registration for future process starts.

### Adopting existing settings

To adopt a `Configuration.Writable` JSON or YAML file, use `ConfigurationWritableJsonStateCodec<TFragment>` or `ConfigurationWritableYamlStateCodec<TFragment>` as an opt-in legacy decoder. Add the legacy file as a read-only migration input, then copy only its source ID to the writable target with `MigrateSourceAsync`. Keep the original file until target verification succeeds. See [Adopting Configuration.Writable](https://arika0093.github.io/Configlue/en/migration/adopting-configuration-writable/) on the documentation site.

## Advanced Usage

### JSON Schema Support

`JsonSchemaGenerator.Generate` and `Write` export versioned schemas from a model's generated `ConfiglueSchema`; pass a source-generated `IJsonTypeInfoResolver` for trimming and NativeAOT-friendly metadata. `TryWriteFromCommandLine` handles the legacy `--cw-generate-json-schema <directory>` option and returns diagnostics without terminating the host. Supported DataAnnotations are mapped to schema constraints.

### Support NativeAOT

With source-generated JSON metadata and the fragment schema, Configlue works in NativeAOT environments. Pass a `JsonSerializerContext` to the codec and, for YAML, generated serializer options with the fragment schema. For more details, refer to the `example/Example.ConsoleApp.NativeAot` project.

### Testing

The testing helpers live in the separate `Configlue.Testing` package with in-memory resources and test doubles:

```shell
dotnet add package Configlue.Testing
```

Compose `SerializedStateSource.FromResource` over an `InMemoryResource` to test resolution, writes, and watchers without touching the file system.

### Interfaces

* `IReadOnlyOptions<T>` — async reads (`GetValueAsync`/`ReadAsync`) and `OnChange`.
* `IWritableOptions<T>` — adds generated Patch saves and `OpenEditSessionAsync`.
* `IConfiglueOptions<T>` — advanced diagnostics, source explanations, reload failures, source patch batches, and source/storage migration. It also exposes synchronous `CurrentValue`.
* `IConfiglueOptionsRegistry<T>` — runtime `TryAdd`/`Get`/`TryRemoveAsync` for dynamic named options.
* `IConfiglueProfiledOptions<T>` — persisted named profiles with active-profile selection.
* Optional compatibility adapters — install `Configlue.Extensions.MSOptions` and call `AddConfiglueMicrosoftOptions<T>()` to register `IOptions<T>`, `IOptionsSnapshot<T>`, and `IOptionsMonitor<T>` for class models. Dynamic names resolve through the registry and `IOptionsMonitor`, not keyed services.

## Packages

| Package | Purpose |
| --- | --- |
| `Configlue` | User-facing meta-package: Core, DI integration, JSON provider, HTTP resources, environment source, and the generator analyzer. Contains no implementation assembly of its own. |
| `Configlue.Abstraction` | Provider, codec, resource, and generated-model contracts. |
| `Configlue.Core` | State resolution and persistence runtime. |
| `Configlue.Extensions.DI` | Dependency-injection registration for Configlue options. |
| `Configlue.Extensions.MSOptions` | Optional Microsoft `IOptions<T>`, `IOptionsSnapshot<T>`, and `IOptionsMonitor<T>` adapters. |
| `Configlue.Generator` | Generated sparse model support (Roslyn analyzer). |
| `Configlue.Testing` | In-memory resources and test doubles. |
| `Configlue.Provider.Json` | JSON codec, section resources, file registrations, and JSON Schema export. |
| `Configlue.Provider.Xml` | XML codec with section resources and file registrations. |
| `Configlue.Provider.Yaml` | YAML codec with section resources and file registrations. |
| `Configlue.Source.Environment` | Read-only source backed by process environment variables. |
| `Configlue.Source.CommandLine` | Read-only source backed by a `System.CommandLine` parse result. |
| `Configlue.Source.Common` | Global/local/file/environment/command-line source presets. |
| `Configlue.Resource.Http` | HTTP read/write resources with ETag revisions and polling change detection. |
| `Configlue.Resource.Http.AspNetCore` | ASP.NET Core endpoints for serving HTTP resources. |
| `Configlue.Resource.Zip` | Resource view over one entry in a ZIP archive. |

## License

This project is licensed under the Apache-2.0 License.
