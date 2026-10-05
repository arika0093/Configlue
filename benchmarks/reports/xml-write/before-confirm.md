```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5  

```
| Method    | TextLength | Mean       | Error      | StdDev    | Gen0     | Gen1     | Gen2     | Allocated |
|---------- |----------- |-----------:|-----------:|----------:|---------:|---------:|---------:|----------:|
| **Serialize** | **0**          |   **1.433 μs** |  **0.0304 μs** | **0.0201 μs** |   **0.5646** |   **0.0153** |        **-** |   **9.71 KB** |
| **Serialize** | **4096**       |  **12.669 μs** |  **1.4183 μs** | **0.9381 μs** |   **2.3804** |   **0.1068** |        **-** |  **40.19 KB** |
| **Serialize** | **65536**      | **251.529 μs** | **12.8747 μs** | **7.6615 μs** | **153.3203** | **153.3203** | **153.3203** | **589.54 KB** |
