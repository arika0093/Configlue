```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IJPESX : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=5  LaunchCount=1  WarmupCount=3  

```
| Method           | PayloadSize | Mean        | Error       | StdDev      | Gen0    | Gen1    | Gen2    | Allocated |
|----------------- |------------ |------------:|------------:|------------:|--------:|--------:|--------:|----------:|
| **OneStageAesWrite** | **0**           |    **535.6 ns** |     **8.85 ns** |     **1.37 ns** |  **0.0105** |       **-** |       **-** |     **192 B** |
| **OneStageAesWrite** | **100**         |    **595.7 ns** |     **4.26 ns** |     **1.11 ns** |  **0.0277** |       **-** |       **-** |     **488 B** |
| **OneStageAesWrite** | **4096**        |  **1,424.7 ns** |   **112.14 ns** |    **17.35 ns** |  **0.7229** |  **0.0324** |       **-** |   **12472 B** |
| **OneStageAesWrite** | **65536**       | **42,459.3 ns** | **6,410.91 ns** | **1,664.89 ns** | **41.6260** | **41.6260** | **41.6260** |  **196829 B** |
