# Configlue

Configlue is a source-generator-first library for reading, resolving, editing, and persisting typed configuration from independent sources.

The repository is being rebuilt around backend-neutral state and sparse generated fragments. See [the architecture notes](docs/architecture.md) for the design direction and current implementation status.

## Projects

- `Configlue`: user-facing package with the runtime, dependency injection, and source generator.
- `Configlue.Abstraction`: provider, codec, resource, and generated-model contracts.
- `Configlue.Core`: state resolution, persistence, and dependency injection runtime.
- `Configlue.Generator`: generated sparse model support.
- `Configlue.Testing`: in-memory resources and test doubles.
- `Configlue.Provider.*`: JSON, XML, and YAML codecs.

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
Generated `TModel.Patch` values can be sent through `IWritableOptions<TModel>.ApplyPatchAsync`; `Unset` removes only the write source's contribution and exposes lower-priority values again.
Named profiles can use keyed DI registrations, for example `AddConfiglueOptions<TModel, TModel.Fragment>("profile", sourceSet)` and `GetRequiredKeyedService<IReadOnlyOptions<TModel>>("profile")`.
For profiles created during runtime, register `AddConfiglueOptionsRegistry<TModel, TModel.Fragment>(...)`, then use `IConfiglueOptionsRegistry<TModel>.TryAdd`, `Get`, and `TryRemove`.
File resources keep one atomic `.bak` generation by default; `FileResourceOptions` can retain more generations in a chosen directory, and `RestoreLatestBackupAsync` restores the newest one explicitly.
Use `StateSourceProjection.Project` to migrate and map a source-specific fragment into a nested model fragment; provide a reverse projection to enable writes to that source.
Use `SerializedStateSource.FromResource<T>` to compose a resource and a codec into a typed source with automatic writer and watcher detection.
Change notifications are debounced by 300ms by default; pass `onChangeDebounce: TimeSpan.Zero` to a registration to disable it.
`IWritableOptions<T>.MigrateSourceAsync(sourceId, targetId)` copies one source contribution, applies its schema migration chain, and writes it to a selected destination.
Configure sessions compare the full source revision vector immediately before saving and fail with `StateConflictException` if any participating source changed.
`JsonSectionResource` exposes a nested JSON path such as `App:Settings` as a separate resource and preserves its sibling values on writes.

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

// Update a deep clone of the current value without opening a session explicitly.
await serviceProvider
    .GetRequiredService<IWritableOptions<AppConfig>>()
    .SaveAsync(settings => settings.SomeSetting = newValue);
```
