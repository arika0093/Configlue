# Configlue

Configlue is a source-generator-first library for reading, resolving, editing, and persisting typed configuration from independent sources.

The repository is being rebuilt around backend-neutral state and sparse generated fragments. See [the architecture notes](docs/en/reference/design-notes.md) for the design direction and current implementation status.

Browse the [Configlue documentation site](https://arika0093.github.io/Configlue/) for setup guides, provider information, and API concepts.

## Projects

- `Configlue`: user-facing meta-package that brings in the Abstraction contracts, Core, dependency injection, the JSON provider, HTTP resources, the environment source, and the source-generator analyzer. It contains no implementation assembly of its own.
- `Configlue.Abstraction`: provider, codec, resource, and generated-model contracts.
- `Configlue.Core`: state resolution and persistence runtime.
- `Configlue.Extensions.DI`: dependency-injection registration and Microsoft options adapters.
- `Configlue.Generator`: generated sparse model support.
- `Configlue.Testing`: in-memory resources and test doubles.
- `Configlue.Provider.*`: JSON, XML, and YAML codecs with their section resources.
- `Configlue.Source.Environment`: a read-only source backed by process environment variables.
- `Configlue.Source.CommandLine`: a read-only source backed by an existing `System.CommandLine` parse result.
- `Configlue.Source.Common`: optional global/local/file/environment/command-line source presets.
- `Configlue.Resource.Zip`: a resource view over one entry in a ZIP archive.
- `Configlue.Resource.Http`: HTTP read/write resources with ETag revisions and polling change detection.
- `Configlue.Resource.Http.AspNetCore`: optional ASP.NET Core endpoints for serving HTTP resources.

## Build

Requires the .NET 10 SDK.

```sh
dotnet build Configlue.slnx
dotnet test --solution Configlue.slnx --configuration Release
```

## Runtime

The generated-model facade provides a one-type-argument registration for both dependency injection and non-DI use. Non-DI applications can create an owned context or initialize one process-wide default; both expose asynchronous reads and writes.

```csharp
await using var context = global::Configlue.Configlue.CreateContext(conf =>
{
    conf.Add<UserSettings>(settings =>
    {
        settings.Sources(sources => sources.Add(CreateUserSettingsSource()));
        settings.WriteRoute = StateWriteRoute.To("user-settings");
    });
});

var options = context.GetOptions<UserSettings>();
var current = await options.GetValueAsync();
await options.SaveAsync(settings => settings.Name = "new name");
```

The static convenience class has the fully qualified name `global::Configlue.Configlue` because it shares a name with the root namespace. `global::Configlue.Configlue.Initialize(...)` and `global::Configlue.Configlue.GetOptions<T>()` provide a process-wide default context; call `await global::Configlue.Configlue.ShutdownAsync()` to dispose it. A `ConfiglueContext` owns the options and watcher tasks it creates. Source, reader, writer, and resource instances supplied by the application remain caller-owned. The DI equivalent is `services.AddConfiglue(conf => conf.Add<UserSettings>(...))`; it uses the same model and source definitions, and the service provider owns the context. Set `OptionsName` in the model callback for a named instance.

`ConfiglueStandardPaths.GetStandardSaveDirectory(applicationId)` returns the platform-standard per-user configuration directory plus the application identifier. The application chooses the file name. A property can map to a specific environment variable with `[ConfiglueEnvironment("ENV_NAME")]`; otherwise the environment source uses its configured prefix and double-underscore member paths.

Register generated model options with a prioritized state-source set. Reads merge the present members from each source, and writes can target a source independently of read priority. The optional `Configlue.Source.Common` package composes global, local, explicitly selected, and environment layers for either `CreateContext` or `services.AddConfiglue`:

```csharp
using Configlue.Source.Common;

config.Add<AppSettings>(model => model.UseCommonSources(new CommonSourceOptions
{
    ApplicationId = "ExampleApp",
    GlobalFileName = "settings.json",
    SpecificFilePath = selectedPath, // selected file path, separate from member overrides
    EnvironmentPrefix = "EXAMPLE",
    CommandLineParseResult = parseResult,
    ConfigureCommandLineMappings = mappings => mappings.Map(portOption, "Server.Port"),
    WriteLayer = CommonSourceWriteLayer.Global,
}));
```

`UseCommonSources` expands to these stable logical sources:

| Source ID | Priority | Included when | Location or input | Writable |
| --- | ---: | --- | --- | --- |
| `common.global` | 100 | `EnableGlobalFile` | `Path.Combine(GetStandardSaveDirectory(ApplicationId), GlobalFileName)` | Only when selected by `WriteLayer` |
| `common.local` | 200 | `EnableLocalFile` | `Path.GetFullPath(LocalFilePath ?? Path.Combine(Environment.CurrentDirectory, GlobalFileName))` | Only when selected by `WriteLayer` |
| `common.specific` | 300 | `EnableSpecificFile` and `SpecificFilePath` is set | `Path.GetFullPath(SpecificFilePath)` | Only when selected by `WriteLayer` |
| `common.environment` | 400 | `EnableEnvironment` and `EnvironmentPrefix` is set | Environment variables | No |
| `common.commandLine` | 500 | `EnableCommandLine` and `CommandLineParseResult` is set | Mappings from the existing parse result | No |

Higher priorities win for members present in more than one source. File sources fall through when the file is missing; other read failures propagate. Exactly the file selected by `WriteLayer` is writable, and selecting a disabled or unavailable file layer throws during registration. The command-line selection of `SpecificFilePath` is separate from member-level mappings; a parse result requires `ConfigureCommandLineMappings`. Set the `Enable*` switches to omit layers, or set `LocalFilePath`, `SpecificFilePath`, and `WriteLayer` to change their locations and destination.
Provider packages add one-call source registrations to the shared `Sources` builder. File helpers work in non-DI and DI contexts; a generated file resource belongs to the context and is disposed after its watcher stops, while directly supplied HTTP clients remain owned by the caller or their factory.

```csharp
using Configlue.Provider.Json;
using Configlue.Provider.Yaml;

await using var context = global::Configlue.Configlue.CreateContext(config =>
{
    config.Add<AppSettings>(model =>
    {
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
    });
});
```

Use the same `config.Add<AppSettings>(...)` definition inside `services.AddConfiglue(...)`. `FromXmlFile(new() { ... })` uses the same options for an XML file and optional element path. For HTTP, pass a direct `HttpClient` outside DI or set `ClientFactory = provider => provider!.GetRequiredService<IHttpClientFactory>().CreateClient("settings")` in DI. HTTP writes are disabled unless `Writable = true`; provide a codec such as `new JsonStateCodec()` explicitly. `Sources(sources => sources.Add(existingSource))` remains available when an application needs a custom reader, writer, watcher, or resource lifecycle.

A model can opt in to dynamic named options with `model.EnableDynamicOptions = true`. The context exposes `GetOptionsRegistry<TModel>()`; `TryAdd(name)` creates the same source/model configuration under that `OptionsName`, and `TryRemoveAsync(name)` stops its watcher and disposes helper-created resources before returning. Fixed registration names are reserved. Removal prevents new operations from starting on that runtime, waits for operations already in progress and the watcher to stop, then disposes helper-created resources. New context lookups fail after removal. Previously returned handles become disposed; a configure session saved after its options were removed fails with `ObjectDisposedException`.

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

In DI, `IOptionsMonitor<AppSettings>.Get("tenant-a")` follows additions and removals through the registry; `Get` throws after removal. Resolve dynamic writable options through `IConfiglueOptionsRegistry<AppSettings>.Get(name)`; keyed services are fixed when the provider is built and are not created for later names. An already materialized `IOptionsSnapshot<T>` keeps its value for that scope, as snapshots normally do. Dynamic named options are runtime-only; persisted profile catalogs are available separately through `EnableProfiles`. Profile names are also names in the options registry and must not collide with fixed `OptionsName` registrations. `SourcesForOptions` runs for each constructed named runtime, including the fixed registration itself, and receives that runtime's exact `OptionsName`. Use it to build profile-specific sources:

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

The profile catalog source must be writable and remains caller-owned. For DI, use the same `EnableProfiles` and `SourcesForOptions` calls inside `services.AddConfiglue(...)`, then resolve `IConfiglueProfiledOptions<AppSettings>` from the provider. Profile names added after provider construction resolve through `IOptionsMonitor` and `IConfiglueOptionsRegistry`, not keyed services. The one-arity non-DI entry is `context.GetProfiledOptions<AppSettings>()` as shown above.

The source set is fixed for each options runtime. A dynamic named-options addition creates a separate runtime from its registration definition, and removal retires that whole runtime; neither operation changes another runtime's source topology. The storage-migration API can retire selected sources after verified migration. To replace a runtime's source set, build a replacement context from the new definitions and coordinate the handoff in the application: stop new operations through the old options, route callers to the replacement, drain in-flight operations, then dispose the old context. Handles already returned from the old context stay attached to its old runtime; dispose that context after callers stop using those handles. The handoff is application-coordinated and is not atomic across contexts.
For DI-backed registration, the `(provider, sources) => ...` overload of `AddConfiglueOptions<TModel, TFragment>` resolves provider services and adds them with `sources.Add(id, reader, priority, fallbackCondition)`. Writer and watcher interfaces implemented by the reader are detected automatically; use `WithWriter` or `WithWatcher` for separate services. The callback runs when the options singleton is created. The builder is extensible, so provider packages can add their own source-registration extension methods.
Register `IStateSchemaMigration<TFragment>` implementations as services to migrate older fragments with the same generated shape. For renamed or removed historical fields, declare earlier model types with `[ConfigluePreviousVersion(typeof(SettingsV1))]` on the current model and pass `Settings.CreateSchemaDispatcher(...)` to `SerializedStateSource.FromResource`; this decodes by schema metadata before current-fragment conversion and preserves sparse presence.
Use `AddConfiglueValidator<T>(IValidateOptions<T>)` for a Microsoft options validator, or pass `validateDataAnnotations: true` when registering options.
Class-model registrations also provide `IOptions<T>`, scoped `IOptionsSnapshot<T>`, and `IOptionsMonitor<T>` adapters. Their synchronous `Value` and `Get` calls read Configlue state synchronously; use `ReadAsync` or `GetValueAsync` in asynchronous application flows. String-keyed profiles and runtime registry profiles resolve by options name.
Generated `TModel.Patch` values can be sent through `IWritableOptions<TModel>.ApplyPatchAsync`; `Unset` removes only the write source's contribution and exposes lower-priority values again.
Use `IWritableOptions<TModel>.ApplyPatchesAsync` with `StateSourcePatch` entries for an explicit source-local multi-write. Disjoint section updates sharing a `ResourceId` are persisted with one physical write by the built-in resources; overlapping scopes are rejected. The result reports each source revision and physical write count, and writes across different resources are not atomic.
Named profiles can use keyed DI registrations, for example `AddConfiglueOptions<TModel, TModel.Fragment>("profile", sourceSet)` and `GetRequiredKeyedService<IReadOnlyOptions<TModel>>("profile")`.
For profiles created during runtime, register `AddConfiglueOptionsRegistry<TModel, TModel.Fragment>(...)`, then use `IConfiglueOptionsRegistry<TModel>.TryAdd`, `Get`, and `TryRemove`.
Use `AddConfiglueProfiledOptions<TModel, TFragment>(profileSourceSetFactory, catalogSourceFactory)` when profile names and the active profile must survive restarts. The catalog is stored through a normal writable `StateSource<ConfiglueProfileCatalog>`, so its provider can be chosen independently. `IConfiglueProfiledOptions<TModel>` lazily restores or creates the default profile on its first async operation, can copy a profile with `CreateProfileAsync`, and persists active-profile changes. Removing a profile removes it from the catalog and runtime; its backing state is retained.
File resources keep one atomic `.bak` generation by default; `FileResourceOptions` can retain more generations in a chosen directory, and `RestoreLatestBackupAsync` restores the newest one explicitly.
Use `StateSourceProjection.Project` to migrate and map a source-specific fragment into a nested model fragment; provide a reverse projection to enable writes to that source.
Use `SerializedStateSource.FromResource<T>` to compose a resource and a codec into a typed source with automatic writer and watcher detection.
Use `FallbackStateSource<TFragment>` to group serialized representations of the same logical state, such as a canonical JSON file and a legacy YAML file. It reads the first successful candidate by priority, subject to each candidate's fallback condition, and exposes that candidate as one source, so values from separate formats are never overlaid. By default, writes go to the active writable candidate, or the highest-priority writable candidate when none is active; set `writeSourceId` to route edits to a fixed candidate such as the canonical file. This does not copy state on creation or delete the other representations, and the candidate sources and resources remain caller-owned.
Resources can expose a stable `ResourceId` separately from logical source IDs and physical-origin labels; section views inherit the underlying identity, and custom resources can implement `IResourceIdentity` or supply an ID to `SerializedStateSource.FromResource`, `StateSource`, or a section view.
Change notifications are debounced by 300ms by default; pass `onChangeDebounce: TimeSpan.Zero` to a registration to disable it.
`IWritableOptions<T>.MigrateSourceAsync(sourceId, targetId)` copies one source contribution, applies its schema migration chain, and writes it to a selected destination.
`MigrateSourcesToTargetsAsync(sourceIds, targetProjections)` merges only the selected contributions, applies a fragment projection for each destination, and revision-checks and verifies each target. A completed target is skipped on retry; if a later target fails, rerun the migration to resume. Pass `retireSources: true` to remove the selected sources from that options instance after every target verifies and only when virtual resolution proves the effective model stays the same; the result lists them in `RetiredSourceIds`. This changes the running options topology; it does not delete backing data, so remove retired sources from the application's registration for future process starts. Multi-target writes are not atomic.
To adopt a Configuration.Writable JSON or YAML file, use `ConfigurationWritableJsonStateCodec<TFragment>` or `ConfigurationWritableYamlStateCodec<TFragment>` as an opt-in legacy decoder. Select a nested section first with `JsonSectionResource` or `YamlSectionResource`, then wrap it in `SerializedStateReader<TFragment>` and a `StateSource<TFragment>` with no writer. The decoders recognize inline `$version` (and `Version` fallback), default an unmarked object or mapping to version 1, and can map that version to the current Configlue model ID for historical dispatch. `$schema` is stripped as metadata. Empty or whitespace-only YAML is read as an empty sparse fragment. For BOM-encoded YAML, pass the source encoding to the codec; when reading a nested section, also pass it as `textEncoding` to `YamlSectionResource`. Its default remains strict UTF-8. Add the source as a read-only migration input and copy only its source ID to the writable target with `MigrateSourceAsync` or `MigrateSourcesToTargetsAsync`. Keep the original file until target verification succeeds; if migration fails, retry with the same source and target definitions. Remove the old file only through an explicit application decision.

```csharp
var oldFile = new FileResource("./old-settings.json");
var oldSection = new JsonSectionResource(
    oldFile,
    writer: null,
    sectionPath: "ApplicationSettings:Database",
    watcher: null);
var oldReader = new SerializedStateReader<AppSettings.Fragment>(
    oldSection,
    new ConfigurationWritableJsonStateCodec<AppSettings.Fragment>(
        modelId: AppSettings.ConfiglueSchema.ModelId));
var oldSource = new StateSource<AppSettings.Fragment>("legacy", oldReader);
var currentSource = CreateCurrentSettingsSource(); // writable source using the normal Configlue codec

await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
    new StateSourceSet<AppSettings.Fragment>([oldSource, currentSource]),
    StateWriteRoute.To("current"));

// Copy only the old contribution and leave oldFile available for retry/recovery.
await options.MigrateSourceAsync("legacy", "current");
```

The file and section resource above remain application-owned. For a file with historical field shapes, pass a schema dispatcher to `SerializedStateReader<TFragment>` and register a matching legacy codec for each historical fragment.
Configure sessions compare the full source revision vector immediately before saving and fail with `StateConflictException` if any participating source changed.
`IReadOnlyOptions<T>.ExplainAsync("Database.Host")` returns the effective value and the present source contributions from highest to lowest priority.

Edits made through `BeginConfigureAsync` or the updater overloads use generated semantic diffs and update only the selected source contribution. Pass a `StateWritePlan` to route changed property paths to other writable sources; the most specific path wins, and nested model changes can be split across source fragments. Plans validate paths and targets before editing, then verify the fully resolved model and all source revisions before writing. The returned `StateWriteResult.MultiWriteResult` reports per-source revisions and physical write count. Writes across different resources are not atomic.

Unchanged fields retain their existing sparse state. `Append` and `SetUnion` edits are rebased onto each target source's collection segment; edits that require changing values owned by another source or are hidden by a higher-priority source fail with `StateConflictException`.
`JsonSectionResource` exposes a nested JSON path such as `App:Settings` as a separate resource and preserves its sibling values on writes.
`XmlSectionResource` and `YamlSectionResource` provide the same nested-section view for XML elements and YAML mappings, including sibling preservation and whole-resource revision checks.
Section edits require standard JSON or UTF-8 YAML. JSON comments/trailing-comma JSONC are not supported, and section writes reserialize the document: comment, whitespace, quoting, and scalar-style preservation is not guaranteed. YAML input with invalid UTF-8 now fails instead of being replacement-decoded and rewritten as UTF-8.
`ZipEntryResource` exposes one archive entry as a logical resource while retaining the archive's physical identity and revision. Disjoint entry updates can share one batched archive write, and untouched entries remain intact.
`HttpResourceReader` reads from `{root}/get` and can be composed with any state codec. Call `CreateWriter()` and pass the result as `writer:` to `SerializedStateSource.FromResource` only when the endpoint supports updates; HTTP requests use ETags for conditional writes and polling. The optional `Configlue.Resource.Http.AspNetCore` package maps the same protocol over user-provided resource handlers. See the [HTTP resource protocol](docs/en/reference/http-resource-protocol.md).
`JsonSchemaGenerator.Generate` and `Write` export versioned schemas from a model's generated `ConfiglueModelSchema`; pass a source-generated `IJsonTypeInfoResolver` for trimming and NativeAOT-friendly metadata. Supported DataAnnotations are mapped to schema constraints.

`Configlue.Source.CommandLine` accepts the application's existing parse result and explicit symbol-to-path mappings. Parser defaults do not become overrides unless the symbol was explicitly supplied; a parse result with errors fails the source read. Map root and selected subcommand symbols explicitly, and create a new source/context when command-line input changes.

```csharp
using Configlue.Source.CommandLine;
using System.CommandLine;

var commandLineSource = new CommandLineSourceOptions
{
    Id = "command-line",
    ParseResult = parseResult,
    Priority = 500,
};
model.Sources(sources => sources.FromCommandLine(commandLineSource, mappings =>
{
    mappings.Map(portOption, "Server.Port");
    mappings.Map(verboseOption, "Diagnostics.Verbose");
}));
```

`Configlue.Source.Environment.EnvironmentStateSource.FromEnvironment<AppConfig, AppConfig.Fragment>("environment", "APP")` creates a read-only sparse source from process environment variables such as `APP__DATABASE__HOST`. Double underscores separate nested model members; member names are matched case-insensitively. A property annotated with `[ConfiglueEnvironment("ENV_NAME")]` can instead read that mapped variable name case-insensitively, including for nested model properties. Common scalar values use invariant parsing, and a custom parser can handle application-specific types. The reader recalculates a content revision on each read; process environment variables do not provide a watcher.

```csharp
// UserSettingsSource implements IStateReader<AppConfig.Fragment>,
// IStateWriter<AppConfig.Fragment>, and IStateWatcher.
services.AddSingleton<UserSettingsSource>();
services.AddConfiglueOptions<AppConfig, AppConfig.Fragment>(
    (provider, sources) =>
    {
        sources.Add(
            "user-settings",
            provider.GetRequiredService<UserSettingsSource>(),
            priority: 100,
            fallbackCondition: StateFallbackCondition.NotFound,
            physicalOrigin: "user-settings.json");
    },
    StateWriteRoute.To("user-settings"));

var config = await serviceProvider
    .GetRequiredService<IReadOnlyOptions<AppConfig>>()
    .GetValueAsync();

using var changeSubscription = serviceProvider
    .GetRequiredService<IReadOnlyOptions<AppConfig>>()
    .OnChange(updated => Console.WriteLine(updated));

await serviceProvider
    .GetRequiredService<IWritableOptions<AppConfig>>()
    .SaveAsync(updatedConfig);

using var edit = await serviceProvider
    .GetRequiredService<IWritableOptions<AppConfig>>()
    .BeginConfigureAsync();
edit.Value.SomeSetting = newValue;
await edit.SaveAsync();

// Route selected model paths to different writable sources for one edit.
var writePlan = new StateWritePlan(new Dictionary<string, string>
{
    ["Database"] = "database-settings",
    ["Database.Password"] = "secrets",
});
using var routedEdit = await serviceProvider
    .GetRequiredService<IWritableOptions<AppConfig>>()
    .BeginConfigureAsync(writePlan);
routedEdit.Value.Database!.Password = "updated";
var writeResult = await routedEdit.SaveAsync();
var sourceWrites = writeResult.MultiWriteResult;

// Update a deep clone of the current value without opening a session explicitly.
await serviceProvider
    .GetRequiredService<IWritableOptions<AppConfig>>()
    .SaveAsync(settings => settings.SomeSetting = newValue);
```

`SaveAsync(updatedConfig)` replaces the configured write source's complete contribution with that model value, including model defaults. It does not preserve members that were absent from that source. Use `SaveAsync(settings => ...)`, `BeginConfigureAsync`, or `ApplyPatchAsync` when the intent is a sparse edit that retains untouched members. The `SaveAsync(value, writePlan)` overload compares the value with the resolved baseline and routes only changed paths.

Persistent named profiles use a separate writable source for their catalog. The profile source factory receives each profile name, which lets an application store profile values in separate files, sections, or other resources.

```csharp
// ProfileCatalogStore implements IStateReader<ConfiglueProfileCatalog> and
// IStateWriter<ConfiglueProfileCatalog> for the application's chosen backend.
services.AddSingleton<ProfileCatalogStore>();
services.AddConfiglueProfiledOptions<AppConfig, AppConfig.Fragment>(
    (provider, profileName) => CreateProfileSources(provider, profileName),
    provider =>
    {
        var catalogStore = provider.GetRequiredService<ProfileCatalogStore>();
        return new StateSource<ConfiglueProfileCatalog>("profile-catalog", catalogStore, writer: catalogStore);
    });

var profiles = serviceProvider.GetRequiredService<IConfiglueProfiledOptions<AppConfig>>();
await profiles.GetProfileNamesAsync(); // Restores persisted profiles or creates "default".
await profiles.CreateProfileAsync("Work", copyFrom: "default");
await profiles.SetActiveProfileAsync("Work");
var activeConfig = await profiles.GetActiveValueAsync();
```
