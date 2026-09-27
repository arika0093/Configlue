# Benchmarks

Run the runtime benchmarks in Release mode:

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*OptionsRuntimeBenchmarks*'
```

`GetValueAsync` measures a warm read and resolution from one in-memory generated fragment. `FacadeCurrentValue` measures the cached `IReadOnlyOptions<T>.CurrentValue` path, including an independent model clone per access. `MonitorCurrentValue` measures the cached `IOptionsMonitor<T>.CurrentValue` path for a watched source. `PublishChangeAsync` measures the watched path from an in-memory source revision change through re-resolution and listener notification. The latter waits for the listener so each invocation includes the full publish path. All use `MemoryDiagnoser`; benchmark results depend on the machine and runtime and are not CI pass/fail thresholds.
