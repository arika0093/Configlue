```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5  

```
| Method     | MemberCount | Mean     | Error     | StdDev    | Median    | Gen0   | Gen1   | Allocated |
|----------- |------------ |---------:|----------:|----------:|----------:|-------:|-------:|----------:|
| **WarmSchema** | **1**           | **1.011 μs** | **0.6574 μs** | **0.4349 μs** | **0.7784 μs** | **0.1144** |      **-** |   **1.93 KB** |
| ColdSchema | 1           | 1.223 μs | 0.2366 μs | 0.1565 μs | 1.1809 μs | 0.3242 | 0.0038 |   5.49 KB |
| **WarmSchema** | **4**           | **1.775 μs** | **0.1431 μs** | **0.0852 μs** | **1.7824 μs** | **0.2403** |      **-** |   **4.06 KB** |
| ColdSchema | 4           | 2.403 μs | 0.4067 μs | 0.2690 μs | 2.3260 μs | 0.4501 | 0.0038 |   7.63 KB |
| **WarmSchema** | **16**          | **5.792 μs** | **0.7258 μs** | **0.4801 μs** | **5.6878 μs** | **0.6714** |      **-** |  **11.44 KB** |
| ColdSchema | 16          | 6.495 μs | 1.5242 μs | 1.0082 μs | 6.1433 μs | 0.8850 | 0.0153 |     15 KB |
