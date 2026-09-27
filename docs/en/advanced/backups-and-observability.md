---
title: Backups, logging, and diagnostics
description: File backup generations and restore, logging, explanations, and diagnostics.
---

# Backups, logging, and diagnostics

## File backups

File resources keep one atomic `.bak` generation by default. Backups go under `backup/` beside the resource file on Windows and `.backup/` on other platforms; Windows marks the directory and files hidden. `FileResourceOptions` can retain more generations in a chosen directory, and `RestoreLatestBackupAsync` restores the newest one explicitly. Relative `BackupDirectory` values are resolved from the resource file directory; `/` selects the resource file directory itself.

```csharp
var resource = new FileResource(
    "settings.json",
    new FileResourceOptions { BackupMaxCount = 5, BackupDirectory = "my-backups" });
```

Set `BackupMaxCount = 0` to disable backups. Atomic writes (temporary file plus rename) and retryable access keep concurrent saves safe.

Automatic recovery is opt-in. For JSON file sources, enable it through `ResourceOptions`:

```csharp
model.UseJsonFile(new JsonFileSourceOptions
{
    Id = "settings",
    Path = "settings.json",
    ResourceOptions = new FileResourceOptions { AutomaticBackupRecovery = true },
});
```

When the file is missing or contains invalid JSON, Configlue checks the latest backup (for example, `.backup/settings.json.bak` on Linux and macOS) and restores it only if it can be decoded and the file has not changed since the failed read. It also recognizes timestamped backups made by Configuration.Writable and migrates retained generations into the current layout on the next save that creates a backup. The default JSON codec classifies malformed JSON for recovery. Custom codecs must implement `IStateCodecRecoveryPolicy` to recover from format errors; missing-file recovery does not require that policy.

File writes retry transient sharing failures twice by default (3 total attempts), waiting 100ms between attempts. `RetryCount` is the number of retries after the initial attempt; `RetryCount` and `RetryDelay` change those defaults. Set `RetryDelayFactory` to calculate a delay for each one-based retry attempt, for example:

```csharp
var options = new FileResourceOptions
{
    RetryCount = 5,
    RetryDelayFactory = attempt => TimeSpan.FromMilliseconds(100 * attempt),
};
```

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
