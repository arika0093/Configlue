```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IJPESX : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=5  LaunchCount=1  WarmupCount=3  

```
| Method       | DocumentKind         | Mean      | Error      | StdDev    | Gen0   | Gen1   | Allocated |
|------------- |--------------------- |----------:|-----------:|----------:|-------:|-------:|----------:|
| **ReadMetadata** | **Valid**                | **10.523 μs** |  **0.9237 μs** | **0.2399 μs** | **2.5635** | **0.3357** |  **43.51 KB** |
| **ReadMetadata** | **Unmarked**             | **10.359 μs** |  **1.4218 μs** | **0.3692 μs** | **2.5482** | **0.3510** |  **42.96 KB** |
| **ReadMetadata** | **InvalidVersion**       | **17.851 μs** | **10.0173 μs** | **2.6015 μs** | **2.5635** | **0.3052** |  **43.35 KB** |
| **ReadMetadata** | **Empty**                |  **2.349 μs** |  **0.0688 μs** | **0.0107 μs** | **0.1602** |      **-** |    **2.7 KB** |
| **ReadMetadata** | **InvalidChild**         | **14.189 μs** |  **3.5736 μs** | **0.5530 μs** | **2.6550** | **0.3662** |  **44.73 KB** |
| **ReadMetadata** | **TrailingData**         | **11.737 μs** |  **0.2581 μs** | **0.0670 μs** | **2.6245** | **0.3662** |  **44.65 KB** |
| **ReadMetadata** | **UnmarkedTrailingData** | **13.005 μs** |  **0.9857 μs** | **0.1525 μs** | **2.5940** | **0.3052** |   **44.1 KB** |
| **ReadMetadata** | **Inval(...)gData [26]** | **13.773 μs** |  **0.5733 μs** | **0.0887 μs** | **2.6245** | **0.3357** |  **44.49 KB** |
| **ReadMetadata** | **Dtd**                  |  **2.770 μs** |  **0.1024 μs** | **0.0158 μs** | **0.2098** |      **-** |   **3.59 KB** |
| **ReadMetadata** | **UnknownEntity**        |  **4.717 μs** |  **3.3218 μs** | **0.5140 μs** | **0.2441** |      **-** |   **4.22 KB** |
