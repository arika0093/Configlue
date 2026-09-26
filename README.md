# Configlue

Configlue is a source-generator-first library for reading, resolving, editing, and persisting typed configuration from independent sources.

The repository is being rebuilt around backend-neutral state and sparse generated fragments. See [the architecture notes](docs/architecture.md) for the design direction and current implementation status.

## Projects

- `Configlue`: user-facing package with the runtime, dependency injection, and source generator.
- `Configlue.Abstraction`: provider, codec, resource, and generated-model contracts.
- `Configlue.Core`: state resolution, persistence, and dependency injection runtime.
- `Configlue.Generator`: generated sparse model support.
- `Configlue.Testing`: in-memory resources and test doubles.
- `Configlue.Provider.*`: JSON, XML, and YAML codecs, plus ZIP entry resources.

## Build

Requires the .NET 10 SDK.

```sh
dotnet build Configlue.slnx
dotnet test --solution Configlue.slnx --configuration Release
```

## Runtime

Register generated model options with a prioritized state-source set. Reads merge the present members from each source, and writes can target a source independently of read priority.
Register `IStateSchemaMigration<TFragment>` implementations as services to migrate older source fragments while reading them.
Use `AddConfiglueValidator<T>(IValidateOptions<T>)` for a Microsoft options validator, or pass `validateDataAnnotations: true` when registering options.
Class-model registrations also provide `IOptions<T>`, scoped `IOptionsSnapshot<T>`, and `IOptionsMonitor<T>` adapters. Their synchronous `Value` and `Get` calls read Configlue state synchronously; use `ReadAsync` or `GetValueAsync` in asynchronous application flows. String-keyed profiles and runtime registry profiles resolve by options name.
Generated `TModel.Patch` values can be sent through `IWritableOptions<TModel>.ApplyPatchAsync`; `Unset` removes only the write source's contribution and exposes lower-priority values again.
Use `IWritableOptions<TModel>.ApplyPatchesAsync` with `StateSourcePatch` entries for an explicit source-local multi-write. Disjoint section updates sharing a `ResourceId` are persisted with one physical write by the built-in resources; overlapping scopes are rejected. The result reports each source revision and physical write count, and writes across different resources are not atomic.
Named profiles can use keyed DI registrations, for example `AddConfiglueOptions<TModel, TModel.Fragment>("profile", sourceSet)` and `GetRequiredKeyedService<IReadOnlyOptions<TModel>>("profile")`.
For profiles created during runtime, register `AddConfiglueOptionsRegistry<TModel, TModel.Fragment>(...)`, then use `IConfiglueOptionsRegistry<TModel>.TryAdd`, `Get`, and `TryRemove`.
File resources keep one atomic `.bak` generation by default; `FileResourceOptions` can retain more generations in a chosen directory, and `RestoreLatestBackupAsync` restores the newest one explicitly.
Use `StateSourceProjection.Project` to migrate and map a source-specific fragment into a nested model fragment; provide a reverse projection to enable writes to that source.
Use `SerializedStateSource.FromResource<T>` to compose a resource and a codec into a typed source with automatic writer and watcher detection.
Resources can expose a stable `ResourceId` separately from logical source IDs and physical-origin labels; section views inherit the underlying identity, and custom resources can implement `IResourceIdentity` or supply an ID to `SerializedStateSource.FromResource`, `StateSource`, or a section view.
Change notifications are debounced by 300ms by default; pass `onChangeDebounce: TimeSpan.Zero` to a registration to disable it.
`IWritableOptions<T>.MigrateSourceAsync(sourceId, targetId)` copies one source contribution, applies its schema migration chain, and writes it to a selected destination.
`MigrateSourcesToTargetsAsync(sourceIds, targetProjections)` merges only the selected contributions, applies a fragment projection for each destination, and revision-checks and verifies each target. A completed target is skipped on retry; if a later target fails, rerun the migration to resume. Pass `retireSources: true` to remove the selected sources from that options instance after every target verifies and only when virtual resolution proves the effective model stays the same; the result lists them in `RetiredSourceIds`. This changes the running options topology; it does not delete backing data, so remove retired sources from the application's registration for future process starts. Multi-target writes are not atomic.
Configure sessions compare the full source revision vector immediately before saving and fail with `StateConflictException` if any participating source changed.
`IReadOnlyOptions<T>.ExplainAsync("Database.Host")` returns the effective value and the present source contributions from highest to lowest priority.

Edits made through `BeginConfigureAsync` or the updater overloads use generated semantic diffs and update only the selected source contribution. Pass a `StateWritePlan` to route changed property paths to other writable sources; the most specific path wins, and nested model changes can be split across source fragments. Plans validate paths and targets before editing, then verify the fully resolved model and all source revisions before writing. The returned `StateWriteResult.MultiWriteResult` reports per-source revisions and physical write count. Writes across different resources are not atomic.

Unchanged fields retain their existing sparse state. `Append` and `SetUnion` edits are rebased onto each target source's collection segment; edits that require changing values owned by another source or are hidden by a higher-priority source fail with `StateConflictException`.
`JsonSectionResource` exposes a nested JSON path such as `App:Settings` as a separate resource and preserves its sibling values on writes.
`XmlSectionResource` and `YamlSectionResource` provide the same nested-section view for XML elements and YAML mappings, including sibling preservation and whole-resource revision checks.
`ZipEntryResource` exposes one archive entry as a logical resource while retaining the archive's physical identity and revision. Disjoint entry updates can share one batched archive write, and untouched entries remain intact.
`JsonSchemaGenerator.Generate` and `Write` export versioned schemas from a model's generated `ConfiglueModelSchema`; pass a source-generated `IJsonTypeInfoResolver` for trimming and NativeAOT-friendly metadata. Supported DataAnnotations are mapped to schema constraints.

```csharp
services.AddConfiglueOptions<AppConfig, AppConfig.Fragment>(
    sourceSet,
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
