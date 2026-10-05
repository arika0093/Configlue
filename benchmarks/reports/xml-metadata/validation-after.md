```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IJPESX : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=5  LaunchCount=1  WarmupCount=3  

```
| Method       | DocumentKind         | Mean      | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|------------- |--------------------- |----------:|----------:|----------:|-------:|-------:|----------:|
| **ReadMetadata** | **Valid**                |  **9.116 μs** | **0.2791 μs** | **0.0725 μs** | **0.8698** | **0.0458** |  **14.71 KB** |
| **ReadMetadata** | **Unmarked**             |  **7.488 μs** | **0.1040 μs** | **0.0161 μs** | **0.8469** | **0.0381** |  **14.28 KB** |
| **ReadMetadata** | **InvalidVersion**       |  **9.010 μs** | **0.2062 μs** | **0.0319 μs** | **0.8545** | **0.0305** |  **14.59 KB** |
| **ReadMetadata** | **Empty**                |  **2.387 μs** | **0.0474 μs** | **0.0123 μs** | **0.1602** |      **-** |    **2.7 KB** |
| **ReadMetadata** | **InvalidChild**         | **10.679 μs** | **1.3471 μs** | **0.3498 μs** | **0.9460** | **0.0153** |  **15.97 KB** |
| **ReadMetadata** | **TrailingData**         | **11.451 μs** | **0.8627 μs** | **0.2240 μs** | **0.9308** | **0.0458** |  **15.85 KB** |
| **ReadMetadata** | **UnmarkedTrailingData** | **11.478 μs** | **0.9920 μs** | **0.1535 μs** | **0.9155** | **0.0458** |  **15.42 KB** |
| **ReadMetadata** | **Inval(...)gData [26]** | **11.739 μs** | **1.5599 μs** | **0.2414 μs** | **0.9308** | **0.0458** |  **15.73 KB** |
| **ReadMetadata** | **Dtd**                  |  **2.820 μs** | **0.2561 μs** | **0.0665 μs** | **0.2098** |      **-** |   **3.59 KB** |
| **ReadMetadata** | **UnknownEntity**        |  **3.688 μs** | **0.0762 μs** | **0.0118 μs** | **0.2289** |      **-** |   **3.91 KB** |
