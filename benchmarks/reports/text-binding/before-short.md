```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IJPESX : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=5  LaunchCount=1  WarmupCount=3  

```
| Method     | MemberCount | Mean      | Error      | StdDev    | Gen0   | Gen1   | Allocated |
|----------- |------------ |----------:|-----------:|----------:|-------:|-------:|----------:|
| **WarmSchema** | **1**           |  **2.667 μs** |  **1.9202 μs** | **0.4987 μs** | **0.3109** | **0.0038** |   **5.27 KB** |
| ColdSchema | 1           |  1.845 μs |  1.6788 μs | 0.4360 μs | 0.4025 | 0.0057 |    6.8 KB |
| **WarmSchema** | **4**           |  **2.595 μs** |  **0.8238 μs** | **0.2139 μs** | **0.4387** |      **-** |    **7.4 KB** |
| ColdSchema | 4           |  4.702 μs |  4.2137 μs | 1.0943 μs | 0.5302 | 0.0076 |   8.94 KB |
| **WarmSchema** | **16**          | **14.784 μs** |  **3.4645 μs** | **0.8997 μs** | **0.8698** |      **-** |  **14.77 KB** |
| ColdSchema | 16          | 10.236 μs | 13.8675 μs | 3.6013 μs | 0.9613 | 0.0153 |  16.31 KB |
