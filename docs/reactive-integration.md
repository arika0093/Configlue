# Composing configuration with Rx.NET or R3

Install `Configlue.Extensions.Reactive` for System.Reactive 7.0.0, or
`Configlue.Extensions.R3` for R3 1.3.1. Each optional package references only
Configlue.Abstraction and its native reactive library. Core, providers, and ordinary
Configlue applications do not acquire either dependency.

| Adapter | Source | Behavior |
| --- | --- | --- |
| `ObserveChanges()` | `IReadOnlyState<T>` | Future resolved changes, without an initial read. |
| `ObserveValues()` | `IReadOnlyState<T>` | Current resolved value followed by changes. |
| `ObserveReloadFailures()` | `IConfiglueDiagnostics<T>` | Watcher failures emitted as `Exception` values. |
| `ObserveActiveValues()` | `IConfiglueProfiledState<T>` | Current active value, value changes, and profile switches. |
| `ObserveActiveProfileNames()` | `IConfiglueProfiledState<T>` | Current active profile name followed by selection changes. |

Every subscription owns its listener and initial-read cancellation source.
Disposal detaches the listener, cancels an outstanding initial read, and suppresses
late results even when a source ignores cancellation. The source definitions/context
remain caller-owned. Streams are cold: use native `Publish().RefCount()` or
`Share()` when several consumers should share one subscription.

Value adapters subscribe before reading. When a change arrives during attachment
or the initial read, the older initial result is omitted. Notifications are
serialized, including concurrent and reentrant callbacks, without invoking
consumer code under the adapter's lock. There is no implicit dispatcher: use the
library's scheduling operators for UI or dedicated-thread delivery.

An initial read or listener-attachment error terminates the subscription. Rx.NET
reports it through `OnError`; R3 reports `OnCompleted(Result.Failure(exception))`.
Use native retry/recovery operators when appropriate. R3 keeps its native
observer exception handling through `OnErrorResume`. Rx.NET observer exceptions
in future change callbacks follow the Configlue callback primitive's propagation
policy; exceptions during asynchronous initial delivery detach the listener and
are reported with `Trace.TraceError`, without faulting an unobserved background
task or appearing as reload failures. Watcher reload failures are
separate nonterminal values in `ObserveReloadFailures()`. They describe background
reloads, rather than errors from explicit reads or failures in change listeners.

## Rx.NET: combine models, select state, debounce, and dispatch

Assume `app` and `network` are `IReadOnlyState<AppSettings>` and
`IReadOnlyState<NetworkSettings>`, and `dispatcher` is the target
`SynchronizationContext`.

```csharp
using Configlue.Extensions.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;

using var subscription = app.ObserveValues()
    .Select(value => value.Theme)
    .DistinctUntilChanged()
    .CombineLatest(network.ObserveValues(),
        (theme, connection) => new { Theme = theme, connection.Endpoint })
    .Throttle(TimeSpan.FromMilliseconds(100), Scheduler.Default)
    .ObserveOn(dispatcher)
    .Subscribe(ApplyView, ReportReadFailure);

using var reloads = diagnostics.ObserveReloadFailures()
    .ObserveOn(dispatcher)
    .Subscribe(ReportReloadFailure);
```

For deterministic tests, pass a `HistoricalScheduler` to `Throttle` and advance
virtual time. `ObserveOn` controls delivery; it does not change where Configlue
reads the underlying sources.

## R3: the same composition with native observables

```csharp
using Configlue.Extensions.R3;
using R3;

using var subscription = app.ObserveValues()
    .Select(value => value.Theme)
    .DistinctUntilChanged()
    .CombineLatest(network.ObserveValues(),
        (theme, connection) => new { Theme = theme, connection.Endpoint })
    .Debounce(TimeSpan.FromMilliseconds(100), TimeProvider.System)
    .ObserveOn(dispatcher)
    .Subscribe(ApplyView, ReportObserverFailure, result =>
    {
        if (result.IsFailure)
        {
            ReportReadFailure(result.Exception!);
        }
    });
```

A `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` can drive
`Debounce` deterministically. Reload failures remain ordinary exception values;
they are separate from R3's native `OnErrorResume` channel.

## Profile switching and native Switch

Use `profiles.ObserveActiveValues()` to follow active configuration values directly.
For composition that also selects a profile-specific stream, map active names to
native observables and apply `Switch`. Rx.NET example:

```csharp
using var subscription = profiles.ObserveActiveProfileNames()
    .Select(name => Observable.FromAsync(token =>
            profiles.GetProfileAsync(name, token).AsTask())
        .SelectMany(state => state.ObserveValues()))
    .Switch()
    .Subscribe(ApplyProfile, ReportReadFailure);
```

The R3 equivalent uses `Observable.FromAsync(token =>
profiles.GetProfileAsync(name, token))`, followed by `SelectMany` and `Switch`.
Switching disposes the previous profile stream, so later changes from that profile
are detached. Keep the profile manager and its context alive for the subscription.
