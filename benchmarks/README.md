# Benchmarks

Run every benchmark in Release mode:

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks
```

To run one group, pass a BenchmarkDotNet filter:

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*FilePersistenceBenchmarks*'
```

`OptionsRuntimeBenchmarks` measures warm reads, cached facade and `IOptionsMonitor<T>` values, and a watched in-memory source update through listener notification. `LayeredResolutionBenchmarks` measures resolution with 1, 4, and 16 in-memory sources. `FilePersistenceBenchmarks` compares file-backed reads and async saves between Configlue and Configuration.Writable using separate JSON files, each library's default document format, and default backup behavior. `FileResourceReadBenchmarks` and `FileResourceWriteBenchmarks` measure raw resource I/O at 1 KiB, 64 KiB, and 1 MiB without backup rotation. `SerializedFileReadBenchmarks` measures file reads with JSON decoding at the same sizes. All groups use `MemoryDiagnoser`.

BenchmarkDotNet reports elapsed time and allocated bytes for the benchmark process and runtime. Compare results from the same machine, .NET runtime, power mode, and build configuration. File persistence numbers include local file system and OS cache behavior. Results are measurements, not CI thresholds; the two libraries use different document formats, so file size and serialization work are not identical.
