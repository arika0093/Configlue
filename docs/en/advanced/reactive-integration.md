---
title: Rx.NET and R3 integration
description: Compose Configlue values, profile changes, and reload failures as native observable streams.
---

Configlue keeps its core change-notification API callback-based. Applications that already use Rx.NET or R3 can install an optional adapter package without adding either reactive dependency to Configlue Core.

Use `Configlue.Extensions.Reactive` for Rx.NET or `Configlue.Extensions.R3` for R3.

## Available adapters

| Adapter | Source | Stream |
| --- | --- | --- |
| `ObserveChanges()` | `IReadOnlyOptions<T>` | Future effective-value changes, without an initial read |
| `ObserveValues()` | `IReadOnlyOptions<T>` | Current effective value followed by changes |
| `ObserveReloadFailures()` | `IConfiglueDiagnostics<T>` | Background reload failures as exception values |
| `ObserveActiveValues()` | `IConfiglueProfiledOptions<T>` | Active profile value and later profile/value changes |
| `ObserveActiveProfileNames()` | `IConfiglueProfiledOptions<T>` | Current active profile name followed by switches |

The streams are cold. Each subscription attaches its own Configlue listener and owns cancellation for an initial read. Dispose the subscription to detach the listener.

## Rx.NET composition

```csharp
using Configlue.Extensions.Reactive;
using System.Reactive.Linq;

using var subscription = app.ObserveValues()
    .Select(value => value.Theme)
    .DistinctUntilChanged()
    .CombineLatest(
        network.ObserveValues(),
        (theme, connection) => new { Theme = theme, connection.Endpoint })
    .Throttle(TimeSpan.FromMilliseconds(100))
    .Subscribe(ApplyView, ReportReadFailure);
```

Configlue does not select a UI dispatcher. Use the reactive library's scheduling operators when callbacks must run on a specific scheduler or synchronization context.

## Reload failures are a separate stream

`ObserveReloadFailures()` emits background watcher/reload failures as values. They do not terminate the value stream merely because a later reload can succeed.

An error while attaching a listener or performing the initial read does terminate the value subscription. Rx.NET reports it through `OnError`; R3 completes with `Result.Failure`. Use the native retry or recovery operators when that behavior is appropriate.

## Profile switching

`ObserveActiveValues()` follows the active profile. When the active profile changes, the adapter switches to the new profile's value stream and detaches from the previous one.

Use `ObserveActiveProfileNames()` when the application needs to compose the profile name with another stream explicitly.

The source options, profile manager, and owning context remain caller-owned. Keep them alive for at least as long as their subscriptions.
