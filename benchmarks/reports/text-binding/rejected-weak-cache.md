```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5  

```
| Method     | MemberCount | Mean       | Error     | StdDev    | Gen0   | Gen1   | Gen2   | Allocated |
|----------- |------------ |-----------:|----------:|----------:|-------:|-------:|-------:|----------:|
| **WarmSchema** | **1**           |   **758.3 ns** |  **65.30 ns** |  **43.19 ns** | **0.1221** |      **-** |      **-** |   **2.06 KB** |
| ColdSchema | 1           | 1,755.1 ns | 131.73 ns |  87.13 ns | 0.3357 | 0.0057 |      - |   5.68 KB |
| **WarmSchema** | **4**           | **1,763.1 ns** | **115.47 ns** |  **68.71 ns** | **0.2480** |      **-** |      **-** |    **4.2 KB** |
| ColdSchema | 4           | 2,669.1 ns | 252.07 ns | 166.73 ns | 0.4616 | 0.0114 | 0.0038 |   7.82 KB |
| **WarmSchema** | **16**          | **5,294.7 ns** | **367.66 ns** | **218.79 ns** | **0.6866** |      **-** |      **-** |  **11.57 KB** |
| ColdSchema | 16          | 6,130.7 ns | 351.08 ns | 208.92 ns | 0.9003 | 0.0153 |      - |  15.19 KB |
