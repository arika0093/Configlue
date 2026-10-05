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
| **WarmSchema** | **1**           |   **718.1 ns** |  **55.45 ns** |  **36.68 ns** | **0.1221** |      **-** |   **2.06 KB** |
| ColdSchema | 1           | 1,480.0 ns | 446.31 ns | 265.59 ns | 0.3357 | 0.0019 |   5.66 KB |
| **WarmSchema** | **4**           | **1,748.0 ns** | **122.73 ns** |  **81.18 ns** | **0.2480** |      **-** |    **4.2 KB** |
| ColdSchema | 4           | 2,252.4 ns | 167.78 ns | 110.98 ns | 0.4616 | 0.0076 |   7.79 KB |
| **WarmSchema** | **16**          | **5,683.2 ns** | **462.79 ns** | **306.11 ns** | **0.6866** |      **-** |  **11.57 KB** |
| ColdSchema | 16          | 6,105.4 ns | 500.09 ns | 330.78 ns | 0.8926 | 0.0153 |  15.17 KB |
