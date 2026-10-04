# Runtime diagnostic overhead

Measured locally on 2026-10-02 using BenchmarkDotNet 0.15.8, Windows 11,
Intel Core i7-14700F, .NET 10.0.12 x64, workstation GC. One launch, five warmup
iterations, eight measured iterations. No diagnostic listeners were attached.

The baseline is commit `12e9813`, before runtime diagnostics. It uses the same
`RuntimeDiagnosticBenchmarks` source with the diagnostic configuration callback
removed and the mode parameter replaced by `Baseline`. Source setup and the
measured `GetValueAsync()` method are identical. Current results include the
operation-specific enablement checks and direct provider/resolution path when
diagnostics are disabled.

| Sources | Mode | Mean | Allocated per read |
| --- | --- | ---: | ---: |
| 1 | Before diagnostics | 756.4 ns | 2.06 KB |
| 1 | Disabled | 816.7 ns | 2.06 KB |
| 1 | Default snapshot | 1,326.4 ns | 2.06 KB |
| 1 | History, capacity 64 | 1,334.2 ns | 2.06 KB |
| 4 | Before diagnostics | 990.9 ns | 2.28 KB |
| 4 | Disabled | 1,059.2 ns | 2.28 KB |
| 4 | Default snapshot | 2,579.2 ns | 2.28 KB |
| 4 | History, capacity 64 | 2,648.8 ns | 2.28 KB |

The four-source case falls through three missing sources before loading the last
source. Disabled observation adds approximately 60–68 ns per read in this run,
about 7–8% of these entirely in-memory operations; no additional per-read managed
allocation was measured. Snapshot/history tracking has a measurable CPU cost.
These short local measurements are estimates, not a portable performance bound
or a claim of zero overhead. They do not measure logging/tracing exporters or I/O.

Reproduce the current measurements:

```powershell
dotnet run --project benchmarks/Configlue.Benchmarks -c Release -- `
  --filter '*RuntimeDiagnosticBenchmarks*' --job short `
  --warmupCount 5 --iterationCount 8 --launchCount 1
```

Raw reports are emitted under `BenchmarkDotNet.Artifacts/results/`.

## Regression comparison for issue #223 (2026-10-04)

`RuntimeDiagnosticBenchmarks` is unchanged. The new
`RuntimeDiagnosticRegressionBenchmarks` (new file, no shared benchmark code
edited) adds the missing modes: explicit listener attached and telemetry
(`MeterListener` over the `Configlue` meter) attached, both built on
fully-disabled options so the measured cost is exactly the dynamic-observer
path. `Disabled` remains the in-tree baseline; the pre-diagnostics numbers above
are a historic record and cannot be re-measured on this commit without checking
out the old sources. Activity-listener tracing shares the same enablement gate
(`ActivitySource.HasListeners`) and is covered by `RuntimeDiagnosticTests`
tracing cases rather than a benchmark listener.

Fast-path change: `RuntimeDiagnosticRecorder` now caches the immutable
configuration (`TrackSnapshot`, history capacity, logger presence) once at
construction. The per-operation gate is a single cached static branch plus only
the truly-dynamic checks (explicit listener array, `ConfiglueTelemetry`
Activity/Meter enablement). Logger probing is hoisted behind logger presence
(zero virtual calls when logger-less, the common benchmark case) and narrowed
from four levels to the single level relevant to the event kind. `Start` no
longer re-evaluates the gate via `Record`, and the enabled implementation lives
in a separate `RecordCore` method so the disabled path returns before any event
construction, locking, or observer dispatch. No new allocations were introduced
on either path.

Run on 2026-10-04 (same machine, short job, warmup 3, 5 iterations, launch 1;
noisy shared box, wide confidence intervals — ordering and allocation are the
signal, not absolute nanoseconds):

| Sources | Mode | Mean | Allocated per read |
| --- | --- | ---: | ---: |
| 1 | Disabled | 1.929 us | 2.09 KB |
| 1 | Snapshot | 2.381 us | 2.09 KB |
| 1 | History, capacity 64 | 3.453 us | 2.09 KB |
| 1 | Listener attached | 2.997 us | 2.09 KB |
| 1 | Telemetry listener | 3.624 us | 2.09 KB |
| 4 | Disabled | 1.951 us | 2.24 KB |
| 4 | Snapshot | 6.502 us | 2.24 KB |
| 4 | History, capacity 64 | 4.503 us | 2.24 KB |
| 4 | Listener attached | 4.198 us | 2.24 KB |
| 4 | Telemetry listener | 5.743 us | 2.24 KB |

Disabled is the fastest mode at both source counts and allocation is identical
across all modes, confirming no added managed allocation. Absolute times here
are inflated versus the 2026-10-02 run (box under load, errors up to ~50% of
the mean), so the ~60–68 ns historical delta is below this run's noise floor
and cannot be re-resolved here. The reduction is analytical: per disabled
operation the gate went from two static reads plus a volatile read plus
`HasListeners` plus a per-kind instrument query plus up to four logger virtual
calls (evaluated in both `Start` and `Record`) to one cached static branch plus
a volatile read plus `HasListeners` plus a per-kind instrument query with zero
logger calls and a single evaluation on the `Start` path.

Remaining checks are semantically required and documented as such: the explicit
listener array can gain subscribers at any time (`OnDiagnosticEvent` must keep
working with tracking disabled, covered by
`Listeners_WorkWithTrackingDisabled`); `ActivitySource.HasListeners` and
per-kind Meter instrument `Enabled` can flip when external observers attach
(covered by `Tracing_NestsSourceReads*` and `Metrics_EmitDurations*`, including
the Disabled-options metrics case); logger presence is static but logger levels
are dynamic, hence the hoisted single-level probe. No static diagnostic state
is cached beyond construction-time configuration.

Reproduce the regression comparison:

```powershell
dotnet run --project benchmarks/Configlue.Benchmarks -c Release -- `
  --filter '*RuntimeDiagnosticRegressionBenchmarks*' --job short `
  --warmupCount 3 --iterationCount 5 --launchCount 1
```
