```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IJPESX : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=5  LaunchCount=1  WarmupCount=3  

```
| Method       | TextLength | InputShape    | Mean         | Error        | StdDev       | Gen0     | Gen1     | Gen2     | Allocated |
|------------- |----------- |-------------- |-------------:|-------------:|-------------:|---------:|---------:|---------:|----------:|
| **Deserialize**  | **0**          | **ArraySlice**    |   **4,414.8 ns** |  **1,481.97 ns** |    **384.86 ns** |   **0.8240** |        **-** |        **-** |  **13.93 KB** |
| ReadMetadata | 0          | ArraySlice    |     982.1 ns |     94.53 ns |     24.55 ns |   0.2365 |   0.0029 |        - |      4 KB |
| **Deserialize**  | **0**          | **Segmented**     |   **5,049.4 ns** |  **1,141.26 ns** |    **176.61 ns** |   **0.8240** |        **-** |        **-** |  **14.18 KB** |
| ReadMetadata | 0          | Segmented     |     990.1 ns |    241.31 ns |     62.67 ns |   0.2518 |   0.0029 |        - |   4.25 KB |
| **Deserialize**  | **0**          | **MemoryManager** |   **4,372.7 ns** |  **1,384.69 ns** |    **359.60 ns** |   **0.8392** |   **0.0153** |        **-** |  **14.18 KB** |
| ReadMetadata | 0          | MemoryManager |     937.2 ns |    128.18 ns |     19.84 ns |   0.2518 |   0.0019 |        - |   4.25 KB |
| **Deserialize**  | **4096**       | **ArraySlice**    |  **16,689.7 ns** |  **4,992.29 ns** |  **1,296.48 ns** |   **3.1738** |   **0.4883** |        **-** |  **53.56 KB** |
| ReadMetadata | 4096       | ArraySlice    |  10,005.0 ns |  1,165.98 ns |    302.80 ns |   0.9003 |   0.0458 |        - |  15.39 KB |
| **Deserialize**  | **4096**       | **Segmented**     |  **16,469.9 ns** |  **3,942.13 ns** |    **610.05 ns** |   **3.9063** |   **0.6104** |        **-** |  **65.81 KB** |
| ReadMetadata | 4096       | Segmented     |   8,445.9 ns |    475.89 ns |     73.64 ns |   1.6327 |   0.0763 |        - |  27.64 KB |
| **Deserialize**  | **4096**       | **MemoryManager** |  **13,859.3 ns** |  **6,899.53 ns** |  **1,067.71 ns** |   **3.9063** |   **0.6104** |        **-** |  **65.81 KB** |
| ReadMetadata | 4096       | MemoryManager |   8,482.4 ns |  1,022.48 ns |    158.23 ns |   1.6174 |   0.0610 |        - |  27.64 KB |
| **Deserialize**  | **65536**      | **ArraySlice**    | **198,176.8 ns** | **31,646.68 ns** |  **4,897.36 ns** |  **83.0078** |  **83.0078** |  **83.0078** | **427.66 KB** |
| ReadMetadata | 65536      | ArraySlice    | 150,667.0 ns | 59,874.48 ns |  9,265.64 ns |   1.4648 |        - |        - |  27.39 KB |
| **Deserialize**  | **65536**      | **Segmented**     | **246,254.8 ns** | **40,807.11 ns** | **10,597.48 ns** | **142.5781** | **142.5781** | **142.5781** | **620.53 KB** |
| ReadMetadata | 65536      | Segmented     | 167,775.2 ns | 14,175.55 ns |  3,681.34 ns |  62.2559 |  62.2559 |  62.2559 | 219.66 KB |
| **Deserialize**  | **65536**      | **MemoryManager** | **250,373.9 ns** | **42,886.56 ns** | **11,137.50 ns** | **142.5781** | **142.5781** | **142.5781** | **620.53 KB** |
| ReadMetadata | 65536      | MemoryManager | 174,346.9 ns |  5,615.36 ns |    868.98 ns |  62.2559 |  62.2559 |  62.2559 | 219.66 KB |
