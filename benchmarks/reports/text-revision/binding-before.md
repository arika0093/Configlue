```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5  

```
| Method     | MemberCount | Mean        | Error       | StdDev      | Gen0   | Gen1   | Allocated |
|----------- |------------ |------------:|------------:|------------:|-------:|-------:|----------:|
| **WarmSchema** | **1**           |    **683.4 ns** |    **40.34 ns** |    **26.68 ns** | **0.1144** |      **-** |   **1.93 KB** |
| ColdSchema | 1           |  1,107.6 ns |    78.41 ns |    46.66 ns | 0.3242 | 0.0038 |   5.49 KB |
| **WarmSchema** | **4**           |  **2,942.4 ns** | **1,597.15 ns** | **1,056.41 ns** | **0.2403** |      **-** |   **4.06 KB** |
| ColdSchema | 4           |  2,615.6 ns |   704.26 ns |   465.82 ns | 0.4501 | 0.0038 |   7.63 KB |
| **WarmSchema** | **16**          |  **5,353.7 ns** |   **448.94 ns** |   **267.16 ns** | **0.6714** |      **-** |  **11.44 KB** |
| ColdSchema | 16          | 10,048.2 ns | 3,970.19 ns | 2,626.04 ns | 0.8850 | 0.0153 |     15 KB |
