# Runtime scratch pooling allocation results

Measured locally on 2026-10-03 with BenchmarkDotNet 0.15.8 on Windows 11,
Intel Core i7-14700F, .NET 10.0.12 x64, workstation GC. Both runs used one
launch, one warmup iteration, and three measured iterations. The benchmark
resolves in-memory layered sources and varies the successful source across the
first, middle, and last positions. Each table value is allocated bytes per
read; `MemoryDiagnoser` was enabled.

The before run used commit `5046467` (before runtime scratch pooling) with the
same benchmark parameterization as the after run. The after run used commit
`6df8311`, based on main commit `d2c8af1`. The benchmark source was copied to the
baseline checkout so source counts and success positions matched exactly.

| Sources | Before | After | Reduction |
| ---: | ---: | ---: | ---: |
| 1 | 1.91 KB | 1.91 KB | — |
| 2 | 2.09 KB | 1.98 KB | 0.11 KB |
| 4 | 2.21 KB | 2.06 KB | 0.15 KB |
| 16 | 3.32 KB | 2.89 KB | 0.43 KB |

For each source count, allocated bytes were identical for first, middle, and
last success. The one-source result is unchanged; this path already avoided
source-count-sized fragment and revision arrays. Multi-source scratch buffers
are now returned to pools, while retained revision vectors and watcher
snapshots remain independently owned. ShortRun timings had high variance and
are omitted; these measurements demonstrate the allocation trend, not a
portable performance bound.

Reproduce the after measurements:

```powershell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- `
  --filter '*LayeredResolutionFallbackBenchmarks*' --job Short `
  --warmupCount 1 --iterationCount 3
```

Raw BenchmarkDotNet reports are generated under `BenchmarkDotNet.Artifacts/results/`.
