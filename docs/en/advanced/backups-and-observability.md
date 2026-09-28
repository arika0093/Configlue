---
title: Backups, logging, and diagnostics
description: File backup generations and restore, logging, explanations, and diagnostics.
---

## File backups

Model-backed JSON, XML, and YAML file sources keep one atomic `.bak` generation by default under the persistent per-user state directory: `%LOCALAPPDATA%` on Windows, `~/Library/Application Support` on macOS, and `$XDG_STATE_HOME` or `~/.local/state` on Linux. The default layout is `configlue-backups/{ModelId}.v{Version}/`; backup filenames include a stable hash of the resource path so files with the same name do not overwrite one another. Windows marks the backup directory and files hidden. Standalone `FileResource` instances without model metadata retain the legacy `backup/` (Windows) or `.backup/` (other platforms) location.

`FileResourceOptions` controls the location and retention. Change only the `configlue-backups` directory name with `BackupDirectoryName`, set `BackupRootDirectory` to use a different persistent root (relative paths use the resource file directory), set `IncludeModelVersionInBackupDirectory = false` for a flat layout, or set `BackupDirectoryMode = FileBackupDirectoryMode.ResourceDirectory` to keep backups beside the resource file. `BackupDirectory` remains an exact-directory override; relative values are resolved from the resource file directory, and `/` selects the resource file directory itself. Standalone `FileResource` callers can pass `backupSchema` to its constructor to enable model-version organization. `BackupMaxCount` includes the latest generation; zero disables backups. `RestoreLatestBackupAsync` restores the newest backup explicitly.

```csharp
using Configlue.Provider.Json;
using Configlue.Resources;

model.UseJsonFile(new JsonFileSourceOptions
{
    Path = "settings.json",
    ResourceOptions = new FileResourceOptions
    {
        BackupDirectoryName = "my-backups",
        BackupMaxCount = 5,
    },
});
```

To use a custom root and a flat layout, set `BackupRootDirectory` and `IncludeModelVersionInBackupDirectory = false`. Atomic writes (temporary file plus rename) and retryable access keep concurrent saves safe.

Automatic recovery is opt-in. For JSON file sources, enable it through `ResourceOptions`:

```csharp
using Configlue.Resources;

model.UseJsonFile(new JsonFileSourceOptions
{
    Id = "settings",
    Path = "settings.json",
    ResourceOptions = new FileResourceOptions { AutomaticBackupRecovery = true },
});
```

When the file is missing or contains invalid JSON, Configlue checks the latest backup and restores it only if it can be decoded and the file has not changed since the failed read. It also recognizes backups from the previous resource-directory layout, the old current-directory-relative custom path, and Configuration.Writable timestamped backups. Retained generations move into the current layout on the next save that creates a backup. The default JSON codec classifies malformed JSON for recovery. Custom codecs must implement `IStateCodecRecoveryPolicy` to recover from format errors; missing-file recovery does not require that policy.

File writes retry failures while creating, writing, flushing, or replacing the file twice by default (3 total attempts), waiting 100ms between attempts. Cancellation is not retried. `RetryCount` is the number of retries after the initial attempt; `RetryCount` and `RetryDelay` change those defaults. Set `RetryDelayFactory` to calculate a delay for each one-based retry attempt, for example:

```csharp
var options = new FileResourceOptions
{
    RetryCount = 5,
    RetryDelayFactory = attempt => TimeSpan.FromMilliseconds(100 * attempt),
};
```

## Provenance and diagnostics

* `await options.GetDetailsAsync()` returns a typed snapshot with effective values and per-source contributions from highest to lowest priority. Use it in settings UIs and troubleshooting.
* `IConfiglueDiagnostics<T>.GetDiagnostics()` returns an immutable snapshot of the configured source topology for that options runtime, including source ID, priority, fallback policy, read/write/watch capabilities, physical origin, resource identity, and active state. It also reports the default and property-path write routes; `GetWriteSourceId("Database.Endpoint")` resolves a registration-level route. Per-operation write plans are specific to that operation and are not included. Use `ReadAsync` for the latest read result and revisions, `GetDetailsAsync()` for effective values and their contributing sources, and write results for completed writes.

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
