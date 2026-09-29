---
title: Lifetime and source precedence
description: Choose between process-wide and explicit contexts, and understand deterministic source ordering.
---

Configlue has two lifetime models. They use the same state APIs and source-resolution rules; the difference is who owns the context.

## Process-wide applications

`ConfiglueApp.Initialize(...)` creates one process-wide default context. This form is useful for a CLI or a small application where passing a context through the program would only add plumbing.

```csharp
ConfiglueApp.Initialize(config =>
{
    config.Add<AppSettings>(model =>
        model.UseJsonFile("settings.json"));
});

var state = ConfiglueApp.GetState<AppSettings>();
var value = await state.GetValueAsync();

await ConfiglueApp.ShutdownAsync();
```

Calling `GetState<T>()` before initialization throws `InvalidOperationException`. Initializing while another default context is active also throws. `ShutdownAsync` is idempotent and clears the default context, so a later test or CLI scenario can initialize a fresh one.

Initialization only builds registrations and runtimes. Source reads and writes remain asynchronous.

## Explicit contexts

`ConfiglueApp.CreateContext(...)` returns an independent `ConfiglueContext`. Prefer this form for tests, libraries, dependency injection, multiple configuration environments, or any code where lifetime should be visible in the object graph.

```csharp
await using var context = ConfiglueApp.CreateContext(config =>
{
    config.Add<AppSettings>(model =>
        model.UseJsonFile("settings.json"));
});

var state = context.GetState<AppSettings>();
```

The context owns state runtimes, watchers, and resources created by registration helpers such as `FromJsonFile`. A source, reader, writer, or resource instance supplied by application code remains caller-owned.

## Source precedence

Resolution orders registered sources by descending numeric `Priority`. A higher number is read first. When two sources have the same priority, the source registered earlier wins the tie.

Resolution happens per member rather than per source. If a higher-priority source does not contribute a member, Configlue can continue to the next source according to that source's fallback rules.

`Unset` removes the selected source's contribution. It does not write the model default into that source. After the contribution is removed, the next lower source—or the model defaults—can provide the effective value.

Read priority and write routing are independent. A save can target a lower-priority writable source even while a higher-priority read-only source continues to determine the effective value. Use `GetDetailsAsync()` to see the effective source and editability before presenting a setting as writable.

See [Resolution and merge](../layering/resolution-and-merge.md) for merge modes and provenance, and [Write routing](../layering/write-routing.md) for destination selection.
