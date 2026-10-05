```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5  

```
| Method     | MemberCount | Mean       | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|----------- |------------ |-----------:|----------:|----------:|-------:|-------:|----------:|
| **WarmSchema** | **1**           |   **594.3 ns** |  **36.81 ns** |  **24.34 ns** | **0.0992** |      **-** |   **1.67 KB** |
| ColdSchema | 1           | 1,154.3 ns |  96.31 ns |  57.31 ns | 0.3090 | 0.0038 |   5.23 KB |
| **WarmSchema** | **4**           | **1,510.2 ns** | **111.06 ns** |  **73.46 ns** | **0.2079** |      **-** |   **3.52 KB** |
| ColdSchema | 4           | 2,017.7 ns | 205.90 ns | 122.53 ns | 0.4196 | 0.0057 |   7.09 KB |
| **WarmSchema** | **16**          | **5,006.9 ns** | **303.46 ns** | **200.72 ns** | **0.6180** |      **-** |  **10.48 KB** |
| ColdSchema | 16          | 5,314.4 ns | 314.62 ns | 208.10 ns | 0.8316 | 0.0153 |  14.04 KB |
