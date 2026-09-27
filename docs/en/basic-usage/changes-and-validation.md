---
title: Changes and validation
description: Change notifications, debounce, DataAnnotations, and custom validators.
---

# Changes and validation

## Change detection

```csharp
using var changeSubscription = options.OnChange(updated =>
    Console.WriteLine($">> Settings changed: {updated.Name}"));
```

File, HTTP (polling), and custom watcher sources push updates through the same callback. In DI, `IOptionsMonitor<T>.OnChange` works as well.

Change notifications are debounced by 300ms by default; pass `onChangeDebounce: TimeSpan.Zero` to a registration to disable it, or a larger value to coalesce high-frequency external edits.

Subscribe to background reload failures separately from value changes:

```csharp
using var reloadFailureSubscription = options.OnReloadFailed(exception =>
    logger.LogError(exception, "Configuration reload failed"));
```

The callback receives watcher or reload exceptions. If a changed state resolves to `NotFound` or `Unavailable`, it receives an `InvalidOperationException` describing that status. Explicit `ReadAsync` failures and exceptions thrown by `OnChange` listeners do not use this callback. A failing reload-failure listener is logged and does not stop other listeners or watcher retries.

## Validation

DataAnnotations validation runs on save by default. Set `ValidateDataAnnotations = false` to disable it. Configlue skips reflection-based DataAnnotations validation automatically when the runtime does not support dynamic code, such as NativeAOT; registered validators continue to run.

```csharp
conf.Add<UserSetting>(model =>
{
    model.ValidateDataAnnotations = false; // optional
    // ...sources...
});
```

```csharp
public partial class UserSetting
{
    [Required, MinLength(3)]
    public string Name { get; set; } = "default name";
    [Range(0, 150)]
    public int Age { get; set; } = 20;
}
```

For a Microsoft options validator, register an instance with `AddConfiglueValidator<T>(IValidateOptions<T>)`:

```csharp
services.AddConfiglueValidator<UserSetting>(new UserSettingValidator());
```

Register `IStateSchemaMigration<TFragment>` implementations as services when older fragments share the generated shape — see [Schema migration](../migration/schema-migration.md).

Microsoft options adapters are opt-in through `Configlue.Extensions.MSOptions` and `services.AddConfiglueMicrosoftOptions<T>()`. `IOptions<T>` caches its first value, and each snapshot caches values for its scope. The monitor caches each named value when its sources expose watchers, then replaces it after a successful change notification; each getter returns a clone, and a failed reload leaves the last successful value available. If no source exposes a watcher, `Get` reads the current state on each call because there is no invalidation signal. These adapters use synchronous reads, so use `ReadAsync` or `GetValueAsync` in asynchronous application flows.

## Next steps

* [Files, formats, and sections](../sources/files-and-sections.md) for provider registrations.
* [Backups, logging, and diagnostics](../advanced/backups-and-observability.md) for file safety and `ExplainAsync`.
