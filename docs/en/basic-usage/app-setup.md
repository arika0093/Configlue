---
title: Application setup
description: Non-DI contexts, DI registration, ownership, and named instances.
---

`conf.Add<TModel>(...)` defines one model: its sources, write route, validators, and options name. The same definition works in both setups below.

## Without DI

`ConfiglueApp.CreateContext(...)` creates an independent lifetime-managed context. `ConfiglueApp.Initialize(...)` plus `ConfiglueApp.GetOptions<T>()` share one process-wide default context instead; call `await ConfiglueApp.ShutdownAsync()` to dispose it. `Initialize` is configuration only and does not block on source I/O; reads and writes remain asynchronous.

The process-wide lifecycle is strict and test friendly. `GetOptions<T>()` before `Initialize` throws `InvalidOperationException`, and initializing while a default context is active throws as well. `ShutdownAsync` is idempotent and clears the default context, so it can be followed by another `Initialize` to build a fresh context. Use `CreateContext` when you need several contexts at once, DI, or scoped lifetimes. Source precedence and equal-priority tie behavior are documented in [Resolution and merge](../layering/resolution-and-merge.md).

```csharp
using Configlue.Sources;

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
using Configlue.Sources;

builder.Services.AddConfiglue(conf => conf.Add<UserSettings>(model =>
{
    model.Sources(sources => sources.Add(CreateUserSettingsSource()));
    model.WriteRoute = StateWriteRoute.To("user-settings");
}));
```

The service provider owns the context. Inject `IReadOnlyOptions<T>` / `IWritableOptions<T>`. To use `IOptions<T>` / `IOptionsSnapshot<T>` / `IOptionsMonitor<T>` for a class model, install `Configlue.Extensions.MSOptions` and opt in after registering the model:

```csharp
using Configlue.Extensions.MSOptions;

services.AddConfiglueMicrosoftOptions<UserSettings>();
```

When a source path or provider comes from DI, use the provider-aware callback. Model registration still runs while `IServiceCollection` is mutable; this callback runs when the runtime source set is created:

```csharp
model.ConfigureSources(registration =>
{
    var paths = registration.Services!.GetRequiredService<ISettingsPathProvider>();
    registration.Sources.FromJsonFile(
        new JsonFileSourceOptions { Path = paths.SettingsFile });
});
```

An already materialized `IOptionsSnapshot<T>` keeps its value for that scope, as snapshots normally do.

The facade does not register the model itself as a synchronous snapshot. Inject `IReadOnlyOptions<T>` or `IWritableOptions<T>` and read it asynchronously. If a framework requires Microsoft's synchronous options abstractions, use the opt-in MSOptions adapter described above.

For a custom source in DI, use the `(provider, sources) => ...` overload of `AddConfiglueOptions<TModel, TFragment>` to resolve services and add them with `sources.Add(id, reader, priority, fallbackCondition)`. Writer and watcher interfaces implemented by the reader are detected automatically; use `WithWriter` / `WithWatcher` for separate services. The callback runs when the options singleton is created, and `Sources(sources => sources.Add(existingSource))` remains available for fully custom lifecycles.

```csharp
using Configlue.Sources;

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

## Per-subject models

Use `model.PerSubject<TAccessor>()` for models whose current value depends on a request, user, tenant, or another application context. Register the accessor with DI; the scoped `IReadOnlyOptions<T>` and `IWritableOptions<T>` facade resolves it on every read and save. Each model registration chooses its own accessor, while models without `PerSubject` keep the usual singleton options lifetime.

```csharp
services.AddScoped<CurrentTenantAccessor>();
services.AddConfiglue(conf =>
{
    conf.Add<UserSettings>(model =>
    {
        model.PerSubject<CurrentTenantAccessor>();
        model.Sources(sources => sources.Add(CreateTenantSource()));
    });

    conf.Add<ServerSettings>(model =>
        model.Sources(sources => sources.Add(CreateServerSource())));
});
```

`CurrentTenantAccessor` implements `IConfiglueSubjectAccessor<TenantSubject>` and returns a `TenantSubject : IConfiglueSubject` from `GetCurrentAsync`. The accessor is application-controlled and can use asynchronous services. `ISubjectOptions<T>` remains a singleton entry point for explicit subject views with `.For(subject)`, and inspection, diagnostics, source administration, and edit-session services continue to address the shared runtime.

An accessor can also implement `IConfiglueSubjectChangeSource`. Its notifications tell an `OnChange` subscription to resolve the subject again and bind to that subject's watcher. This is useful when authentication or another context changes within a scope. Without this optional interface, a watcher stays bound to the subject resolved when the subscription was created.

The optional `Configlue.Resource.Http.AspNetCore` package includes request and Blazor authentication accessors. They only adapt framework context into the core subject contract; the core package does not depend on ASP.NET Core or claims:

```csharp
services.AddHttpContextConfiglueSubjectAccessor<TenantSubject>(
    context => new TenantSubject(context.User.FindFirst("tenant")!.Value));

services.AddBlazorAuthenticationConfiglueSubjectAccessor<TenantSubject>(
    (principal, _) => ValueTask.FromResult(
        new TenantSubject(principal.FindFirst("tenant")!.Value)));
```

Select the matching accessor on that model registration with `PerSubject<HttpContextConfiglueSubjectAccessor<TenantSubject>>()` or `PerSubject<BlazorAuthenticationConfiglueSubjectAccessor<TenantSubject>>()`. The Blazor accessor reports authentication-state changes so active options watchers follow the new subject.

## Custom validators

DataAnnotations validation is enabled by default; pass `validateDataAnnotations: false` when registering the model to disable it. For code-based rules, adapt a Microsoft `IValidateOptions<T>` with `AddConfiglueValidator`, or implement `IConfiglueValidator<T>` directly and register it as a DI singleton:

```csharp
services.AddConfiglueValidator<UserSetting>(new UserSettingValidator());
services.AddSingleton<IConfiglueValidator<UserSetting>, UserSettingValidator2>();
```

See [Changes and validation](./changes-and-validation.md).

## Next steps

* [Common layered sources](./common-sources.md) for the standard global/local/specific/environment stack, with command-line overrides available as an opt-in.
* [Files, formats, and sections](../sources/files-and-sections.md) for provider registrations.
