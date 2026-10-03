# JSON codec schema allocation results

Issue #166 compared the schema-bearing `JsonCodecLayoutBenchmarks.Serialize` allocation before and after direct simple-layout serialization.

## Results

Measured with BenchmarkDotNet `ShortRun` (`IterationCount=3`, `LaunchCount=1`, `WarmupCount=3`) in Release mode. Values are managed bytes allocated per operation from `MemoryDiagnoser`.

| Benchmark | Before `5046467` | After `d2c8af1` | Change |
| --- | ---: | ---: | ---: |
| `Serialize`, Simple (schema-bearing fragment) | 1,368 B | 312 B | −1,056 B (−77.2%) |
| `Serialize`, Detailed (schema-bearing fragment) | 312 B | 312 B | no change |
| `SerializeWithoutSchema`, Simple | 136 B | 136 B | no change |
| `SerializeWithoutSchema`, Detailed | 136 B | 136 B | no change |
| `Deserialize`, Simple | 1,192 B | 1,192 B | no change |
| `Deserialize`, Detailed | 896 B | 896 B | no change |

The targeted Simple schema serialization allocates 4.4× fewer bytes per operation. The Detailed envelope and unrelated benchmark cases stayed at the same measured allocation. ShortRun timing variance is high, so this comparison makes no latency claim.

## Conditions

- Before revision: `5046467b56c183060c450a2ec8e08aa90b33f62a` (`5046467`)
- After revision: `d2c8af123da1b1baaa4931f567e93e27ca55f14a` (`d2c8af1`)
- Benchmark command in both worktrees: `dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*JsonCodecLayoutBenchmarks*' --job short`
- BenchmarkDotNet 0.15.8; .NET SDK 11.0.100-rc.1.26425.128; .NET runtime 10.0.12; Windows 11 25H2; Intel Core i7-14700F; x64 RyuJIT; high performance power plan.
- The before revision's `StateByteTransformerPipeline.cs` failed the repository's CSharpier build check because its two `using` directives were out of order. That file received only the equivalent formatting normalization in the temporary before worktree so the benchmark could build. No behavioral source changes were made to either benchmark worktree.

The before/after worktrees used the same benchmark source and harness. Allocation values are measurements, not CI thresholds.
