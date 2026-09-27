---
title: Examples
description: Runnable sample applications shipped with the repository.
---

# Examples

The `example/` directory contains runnable samples. Run them from the repository root.

## File-backed console app (DI)

`Example.ConsoleApp` is the standard DI sample: a generated model backed by a JSON file resource, read and saved through `IWritableOptions<T>`.

```sh
dotnet run --project example/Example.ConsoleApp
dotnet run --project example/Example.ConsoleApp -- --set-name Ada
```

The settings file is written beside the executable.

## File-backed console app (no DI)

`Example.SimpleApp` shows the same workflow without a container by constructing `ConfiglueOptions<TModel, TFragment>` directly.

```sh
dotnet run --project example/Example.SimpleApp
dotnet run --project example/Example.SimpleApp -- --set-name Ada
```

## Worker service

`Example.WorkerService` registers Configlue with the Generic Host. A background worker reads the settings, increments `RunCount`, and saves every five seconds. Press Ctrl+C to stop.

```sh
dotnet run --project example/Example.WorkerService
```

## Multiple sources with HTTP policy

`Example.MultiSource` combines a high-priority HTTP policy source with writable explicit settings and read-only local/global JSON files. Missing or temporarily unavailable HTTP policy falls through to the file layers; permanent HTTP errors surface to the application.

```sh
dotnet run --project example/Example.MultiSource
dotnet run --project example/Example.MultiSource -- --set-name Ada
```

Set `CONFIGLUE_POLICY_URL` to the HTTP resource root to enable the remote policy source. `example/Example.MultiSource/policy.json` is a small fixture for serving locally.

## YAML console app

`Example.ConsoleApp.Yaml` persists the same kind of generated model as YAML with camel-case member names.

```sh
dotnet run --project example/Example.ConsoleApp.Yaml
dotnet run --project example/Example.ConsoleApp.Yaml -- --set-name Ada
```

## NativeAOT console app

`Example.ConsoleApp.NativeAot` uses source-generated `System.Text.Json` metadata and projects the persisted model into Configlue's sparse fragment.

```sh
dotnet publish example/Example.ConsoleApp.NativeAot --configuration Release --runtime linux-x64 --self-contained true
```

Each guide links back here where a runnable reference helps. Start with [Reading, sessions, and patches](../basic-usage/reading-and-writing.md) for the edit patterns used across all samples.
