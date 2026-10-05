```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5  

```
| Method     | MemberCount | Mean     | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|----------- |------------ |---------:|----------:|----------:|-------:|-------:|----------:|
| **WarmSchema** | **1**           | **1.394 μs** | **0.1003 μs** | **0.0664 μs** | **0.3109** | **0.0038** |   **5.27 KB** |
| ColdSchema | 1           | 1.444 μs | 0.0419 μs | 0.0249 μs | 0.4025 | 0.0057 |    6.8 KB |
| **WarmSchema** | **4**           | **2.235 μs** | **0.0585 μs** | **0.0306 μs** | **0.4387** |      **-** |    **7.4 KB** |
| ColdSchema | 4           | 2.481 μs | 0.1454 μs | 0.0961 μs | 0.5302 | 0.0076 |   8.94 KB |
| **WarmSchema** | **16**          | **5.769 μs** | **0.0981 μs** | **0.0513 μs** | **0.8698** | **0.0076** |  **14.77 KB** |
| ColdSchema | 16          | 6.159 μs | 0.2757 μs | 0.1641 μs | 0.9613 | 0.0153 |  16.31 KB |
