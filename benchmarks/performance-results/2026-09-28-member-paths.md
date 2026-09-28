Generated member path lookup
============================

Command: `dotnet run --project benchmarks/Configlue.Benchmarks -c Release -- --filter '*WriteRouteBenchmarks*' --job Short`

BenchmarkDotNet 0.15.8, Windows 11, Intel Core i7-14700F, .NET 10.0.12,
SDK 11.0.100-rc.1.26425.128. ShortRun: one launch, three warmups, three measured
iterations. These small measurements are directional rather than a performance
guarantee.

| Method | Mean | Standard deviation | Allocated per operation |
| --- | ---: | ---: | ---: |
| Compatibility diagnostic string lookup | 31.558 ns | 0.2832 ns | 56 B |
| Precompiled generated identity lookup | 7.995 ns | 0.1760 ns | 0 B |
| Generated route-below lookup | 1.442 ns | 0.0268 ns | 0 B |

The model has two properties containing the same nested schema, with three
routes including a parent and a leaf override. Paths are prepared during setup.
The measurement isolates route lookup; it does not include selector compilation,
path construction, configuration reads, or persistence.
