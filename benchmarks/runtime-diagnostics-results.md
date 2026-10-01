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
