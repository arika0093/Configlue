# Serialized writer allocation comparison

Measured before issue #165 at `a6f4095` and after it at `633dadd` with the same BenchmarkDotNet ShortRun configuration (`3` measurement iterations, `3` warmups) on Windows 11, Intel Core i7-14700F, .NET 10.0.12. The benchmark runs `SerializedWriterAllocationBenchmarks` with 100 B, 4 KiB, and 64 KiB string payloads. The same cleanup hook is used at both revisions and disposes a batch plan when that revision exposes `IDisposable`.

| Path | Payload | Before | After | Change |
| --- | ---: | ---: | ---: | ---: |
| Normal write | 100 B | 1.64 KB | 1.37 KB | −16.5% |
| Normal write | 4 KiB | 18.45 KB | 13.90 KB | −24.7% |
| Normal write | 64 KiB | 258.51 KB | 193.96 KB | −25.0% |
| Batch write | 100 B | 1.93 KB | 1.66 KB | −14.0% |
| Batch write | 4 KiB | 18.74 KB | 14.20 KB | −24.2% |
| Batch write | 64 KiB | 258.80 KB | 194.26 KB | −24.9% |

These are managed bytes per operation from `MemoryDiagnoser`. ShortRun timing has wide confidence intervals, so the comparison is intended to show the allocation change, not a latency threshold. Results are machine/runtime specific.
