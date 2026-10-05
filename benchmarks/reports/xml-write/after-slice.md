```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5  

```
| Method    | TextLength | Mean       | Error     | StdDev    | Gen0    | Gen1    | Gen2    | Allocated |
|---------- |----------- |-----------:|----------:|----------:|--------:|--------:|--------:|----------:|
| **Serialize** | **0**          |   **1.322 μs** | **0.0289 μs** | **0.0172 μs** |  **0.5493** |  **0.0153** |       **-** |   **9.46 KB** |
| **Serialize** | **4096**       |  **11.872 μs** | **0.8336 μs** | **0.5514 μs** |  **1.6479** |  **0.1373** |       **-** |  **27.93 KB** |
| **Serialize** | **65536**      | **234.134 μs** | **3.0156 μs** | **1.9946 μs** | **90.8203** | **90.8203** | **90.8203** | **396.83 KB** |
