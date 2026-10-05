```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IJPESX : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=5  LaunchCount=1  WarmupCount=3  

```
| Method    | TextLength | Mean       | Error       | StdDev      | Gen0     | Gen1     | Gen2     | Allocated |
|---------- |----------- |-----------:|------------:|------------:|---------:|---------:|---------:|----------:|
| **Serialize** | **0**          |   **1.535 μs** |   **0.3841 μs** |   **0.0594 μs** |   **0.5760** |   **0.0153** |        **-** |   **9.71 KB** |
| **Serialize** | **4096**       |  **12.712 μs** |   **3.2577 μs** |   **0.8460 μs** |   **2.3804** |   **0.1068** |        **-** |  **40.19 KB** |
| **Serialize** | **65536**      | **486.386 μs** | **528.3475 μs** | **137.2102 μs** | **153.3203** | **153.3203** | **153.3203** | **589.54 KB** |
