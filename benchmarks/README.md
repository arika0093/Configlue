# Benchmarks

Run every benchmark in Release mode:

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks
```

To run one group, pass a BenchmarkDotNet filter:

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*FilePersistenceBenchmarks*'
```

`OptionsRuntimeBenchmarks` measures warm reads, cached facade and `IOptionsMonitor<T>` values, and a watched in-memory source update through listener notification. `LayeredResolutionBenchmarks` measures configuration resolution with 1, 4, and 16 in-memory sources. `StateSourceResolverBenchmarks` measures the source resolver scanning 1, 4, and 16 sources before finding a value. `FilePersistenceBenchmarks` compares file-backed reads and async saves between Configlue and Configuration.Writable using separate JSON files, each library's default document format, and default backup behavior. `FileResourceReadBenchmarks` and `FileResourceWriteBenchmarks` measure raw resource I/O at 1 KiB, 64 KiB, and 1 MiB without backup rotation. `SerializedFileReadBenchmarks` compares pipeline-preferred and memory-fallback file reads with JSON decoding at the same sizes.

`OptimizationBenchmarks.cs` adds targeted groups used to choose and verify performance work: `ReadValidationBenchmarks` (validation on/off), `LayeredResolutionFallbackBenchmarks` (1/2/4/16 sources, first-source success vs deep fallback), `FragmentMergeBenchmarks` (fragment merge with 1 vs all members present), `NestedModelReadBenchmarks`, `CollectionMergeBenchmarks` (`Append` vs `SetUnion` with 2 and 8 sources), `SaveRoutingBenchmarks` (single-source vs multi-source routed save), `JsonCodecLayoutBenchmarks` (`DocumentLayout.Simple` vs `Detailed` serialize/deserialize), `JsonSectionBenchmarks` (JSONC section read/write), `EnvironmentSourceBenchmarks`, `CommandLineSourceBenchmarks`, and `FileBackupBenchmarks` (backup generations 1/3/10). All groups use `MemoryDiagnoser`.

`AllocationHotPathBenchmarks.cs` measures AES, compression, combined transformer output, streamed fingerprinting, schema-bearing MessagePack serialization/decoding, and Redis identity construction. Payload groups use 100 B, 4 KiB, and 64 KiB where relevant. `StateRevisionVectorBenchmarks` measures construction, internal lookup, and public dictionary-view access for 0, 1, 2, 4, and 16 entries. `LayeredResolutionFallbackBenchmarks` exercises 1/2/4/16 source resolution; codec and fragment groups cover JSON and generated-fragment paths. `MemoryDiagnoser` reports allocated bytes and Gen0/Gen1/Gen2 collections for each benchmark.

Before an allocation optimization, capture its relevant group at the parent revision and again at the candidate revision on the same machine and runtime. Keep the BenchmarkDotNet reports with the review notes; do not treat numbers from different machines or runtime versions as a regression threshold. For a focused comparison:

Issue #165's same-machine before/after allocation results are recorded in [serialized-writer-allocation-results.md](serialized-writer-allocation-results.md). Issue #168's source-count scratch-pooling allocation comparison is recorded in [runtime-scratch-pooling-results.md](runtime-scratch-pooling-results.md).

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*Allocation*'
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*StateRevisionVector*'
```

BenchmarkDotNet reports elapsed time and allocated bytes for the benchmark process and runtime. Compare results from the same machine, .NET runtime, power mode, and build configuration. File persistence numbers include local file system and OS cache behavior. Results are measurements, not CI thresholds; the two libraries use different document formats, so file size and serialization work are not identical.
