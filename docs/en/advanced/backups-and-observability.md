---
title: Backups, logging, and diagnostics
description: File backup generations and restore, logging, explanations, and diagnostics.
---

# Backups, logging, and diagnostics

## File backups

File resources keep one atomic `.bak` generation by default; `FileResourceOptions` can retain more generations in a chosen directory, and `RestoreLatestBackupAsync` restores the newest one explicitly.

```csharp
var resource = new FileResource(
    "settings.json",
    new FileResourceOptions { BackupMaxCount = 5, BackupDirectory = "my-backups" });
```

Set `BackupMaxCount = 0` to disable backups. Atomic writes (temporary file plus rename) and retryable access keep concurrent saves safe.

## Provenance and diagnostics

* `IReadOnlyOptions<T>.ExplainAsync("Database.Host")` returns the effective value and the present source contributions from highest to lowest priority. Use it in settings UIs and troubleshooting.
* `IReadOnlyOptions<T>.ConfigurationInfo` exposes provider-independent metadata such as the effective read path, next write path, format extension, instance name, and section.
* `IReadOnlyOptions<T>.GetDiagnostics()` returns an immutable snapshot of the configured source topology for that options runtime, including source ID, priority, fallback policy, read/write/watch capabilities, physical origin, resource identity, and retired status. It also reports the default and property-path write routes; `GetWriteSourceId("Database.Endpoint")` resolves a registration-level route. Per-operation write plans are specific to that operation and are not included. Use `ReadAsync` for the latest read result and revisions, `ExplainAsync(path)` for effective values and their contributing sources, and write results for completed writes.

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

## Logging

Facade runtimes use `ILoggerFactory` from DI when one is registered. Non-DI callers can set `Logger` on `ConfiglueModelBuilder<TModel>`, and callers constructing `ConfiglueOptions<TModel, TFragment>` directly can pass its optional `logger` argument. Logging is optional; source read decisions, watcher failures, writes, migration outcomes, and revision conflicts use structured metadata such as model, options name, source ID, physical origin, and resource ID. No configuration values are written to logs.

## Next steps

* [Resolution and merge](../layering/resolution-and-merge.md).
* [Packages](../reference/packages.md).
