---
title: Application setup
description: Non-DI contexts, DI registration, ownership, and named instances.
---

# Application setup

`conf.Add<TModel>(...)` defines one model: its sources, write route, validators, and options name. The same definition works in both setups below.

## Without DI

`ConfiglueApp.CreateContext(...)` creates an independent lifetime-managed context. `ConfiglueApp.Initialize(...)` plus `ConfiglueApp.GetOptions<T>()` share one process-wide default context instead (the same default the older `Configlue` static class uses); call `await ConfiglueApp.ShutdownAsync()` to dispose it.

```csharp
await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<UserSettings>(model =>
    {
        model.Sources(sources => sources.Add(CreateUserSettingsSource()));
        model.WriteRoute = StateWriteRoute.To("user-settings");
    });
});

var options = context.GetOptions<UserSettings>();
```

A `ConfiglueContext` owns the options and watcher tasks it creates. Source, reader, writer, and resource instances supplied by the application remain caller-owned — except resources created by provider registration helpers (e.g. `FromJsonFile`), which belong to the context and are disposed after their watcher stops. Set `OptionsName` in the model callback for a named instance.

## With DI

```csharp
builder.Services.AddConfiglue(conf => conf.Add<UserSettings>(model =>
{
    model.Sources(sources => sources.Add(CreateUserSettingsSource()));
    model.WriteRoute = StateWriteRoute.To("user-settings");
}));
```

The service provider owns the context. Inject `IReadOnlyOptions<T>` / `IWritableOptions<T>`. To use `IOptions<T>` / `IOptionsSnapshot<T>` / `IOptionsMonitor<T>` for a class model, install `Configlue.Extensions.MSOptions` and opt in after registering the model:

```csharp
services.AddConfiglueMicrosoftOptions<UserSettings>();
```

An already materialized `IOptionsSnapshot<T>` keeps its value for that scope, as snapshots normally do.

For consumers that need the model itself, set `RegisterAsSingleton = true` on the default model registration. DI creates the model singleton from the current options value when the model is first resolved. The injected model keeps that snapshot after later source changes; use an options interface when a consumer needs current values or change notifications. This setting takes effect with `AddConfiglue` and requires the default options name.

```csharp
builder.Services.AddConfiglue(conf => conf.Add<UserSettings>(model =>
{
    model.RegisterAsSingleton = true;
    model.Sources(sources => sources.Add(CreateUserSettingsSource()));
}));
```

For a custom source in DI, use the `(provider, sources) => ...` overload of `AddConfiglueOptions<TModel, TFragment>` to resolve services and add them with `sources.Add(id, reader, priority, fallbackCondition)`. Writer and watcher interfaces implemented by the reader are detected automatically; use `WithWriter` / `WithWatcher` for separate services. The callback runs when the options singleton is created, and `Sources(sources => sources.Add(existingSource))` remains available for fully custom lifecycles.

```csharp
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
```

## Custom validators

```csharp
services.AddConfiglueValidator<UserSetting>(new UserSettingValidator());
```

DataAnnotations validation is enabled by default. Pass `validateDataAnnotations: false` to disable it. See [Changes and validation](./changes-and-validation.md).

## Next steps

* [Common layered sources](./common-sources.md) for the standard global/local/specific/environment stack, with command-line overrides available as an opt-in.
* [Files, formats, and sections](../sources/files-and-sections.md) for provider registrations.
