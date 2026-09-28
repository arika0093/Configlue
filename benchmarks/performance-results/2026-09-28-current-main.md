# Performance measurements: 2026-09-28 current main

## Environment and method

Measurements used BenchmarkDotNet 0.15.8, Windows 11 25H2, Intel Core i7-14700F, .NET SDK 10.0.401, and .NET 10.0.12. Runs used the High performance power plan and `ShortRun` (one launch, three warmups, three measured iterations). These are exploratory measurements with wide confidence intervals; they are not CI thresholds.

The first current-main run was taken at `ffc51f7` before the validation fast path. A fresh comparison run used `e9b6c53`, the revision before read-time validation and lower-priority model-default resolution were added. The final full suite was run after caching validation metadata and skipping validation work when the model has no relevant attributes.

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks --no-restore -- --job short --filter '*'
dotnet run -c Release --project benchmarks/Configlue.Benchmarks --no-restore -- --job short --filter '*LayeredResolutionBenchmarks*'
```

## Layered resolution

| Sources | `e9b6c53` | Before optimization | After optimization |
| ---: | ---: | ---: | ---: |
| 1 | 310.5 ns, 1.02 KB | 908.8 ns, 4.58 KB | 380.5 ns, 1.45 KB |
| 4 | 549.9 ns, 1.68 KB | 1,166.8 ns, 5.35 KB | 637.2 ns, 2.23 KB |
| 16 | 1,527.0 ns, 4.54 KB | 2,222.0 ns, 8.49 KB | 1,611.1 ns, 5.37 KB |

The optimization caches whether a model's validation metadata exists and skips DataAnnotations validation setup for models without applicable attributes. Compared with the pre-optimization current-main run, allocations fell by 68%, 58%, and 37% for 1, 4, and 16 sources. Latency fell by 58%, 45%, and 28%. The remaining difference from `e9b6c53` includes the additional read-validation and model-default behavior in current main.

## Other current-main scenarios

| Benchmark | Mean | Allocated |
| --- | ---: | ---: |
| `GetValueAsync` | 372.0 ns | 1,488 B |
| `FacadeGetValueAsync` | 386.3 ns | 1,488 B |
| `MonitorCurrentValue` | 5.4 ns | 32 B |
| `PublishChangeAsync` | 5.38 us | 4,078 B |
| File-backed Configlue read | 7.08 us | 2,056 B |
| `Configuration.Writable` cached read | 15.1 ns | 32 B |
| Configlue save | 4.12 ms | 199,310 B |
| `Configuration.Writable` save | 2.27 ms | 13,242 B |

The cached-read and file-backed-read cases do different work. The save benchmarks use different default document formats and local file-system behavior, so their times and allocations are directional rather than a strict serialization comparison. The existing benchmarks show Configlue's save path is an area for future profiling; this task focused on the measured read-validation regression.

## File-save profiling follow-up

A focused ShortRun on the current worktree added a Configlue case with backups disabled to separate backup rotation from the save pipeline. It also exposed a redundant full source resolution in `SaveAsync`; reusing the already validated baseline reduced work without bypassing source revision checks or proposed-state validation. Single-mutation writes also avoid an extra byte-array copy.

| Benchmark | Mean | Allocated |
| --- | ---: | ---: |
| Configlue save, default backups | 4.112 ms | 190.22 KB |
| Configlue save, backups disabled | 2.344 ms | 105.31 KB |
| `Configuration.Writable` save | 2.191 ms | 12.93 KB |

The run used the same machine and ShortRun configuration described above. The before/after numbers are directional because the earlier checked-in result predates this focused run. The optimizations reduce the full-backup case by about 4.5% in allocations compared with the earlier 199,310 B result, while the no-backup case shows that backup work accounts for about 1.77 ms and 85 KB in this scenario. The remaining allocation gap is a separate profiling follow-up.
