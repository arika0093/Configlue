```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5  

```
| Method            | AssignmentCount | RepeatedOrigin | Mean         | Error        | StdDev      | Gen0   | Allocated |
|------------------ |---------------- |--------------- |-------------:|-------------:|------------:|-------:|----------:|
| **UnmatchedRevision** | **0**               | **False**          |     **345.7 ns** |     **24.64 ns** |    **16.30 ns** | **0.0625** |   **1.05 KB** |
| **UnmatchedRevision** | **0**               | **True**           |     **485.1 ns** |    **248.43 ns** |   **164.32 ns** | **0.0625** |   **1.05 KB** |
| **UnmatchedRevision** | **1**               | **False**          |     **670.4 ns** |    **134.44 ns** |    **80.00 ns** | **0.0839** |   **1.43 KB** |
| **UnmatchedRevision** | **1**               | **True**           |     **684.2 ns** |    **140.46 ns** |    **92.91 ns** | **0.0839** |   **1.43 KB** |
| **UnmatchedRevision** | **16**              | **False**          |   **4,390.5 ns** |    **458.41 ns** |   **303.21 ns** | **0.2975** |   **5.03 KB** |
| **UnmatchedRevision** | **16**              | **True**           |   **3,950.7 ns** |    **717.26 ns** |   **426.83 ns** | **0.2975** |   **5.03 KB** |
| **UnmatchedRevision** | **256**             | **False**          | **106,000.1 ns** | **12,893.18 ns** | **8,528.04 ns** | **3.6621** |  **63.35 KB** |
| **UnmatchedRevision** | **256**             | **True**           |  **58,599.1 ns** | **10,756.43 ns** | **6,400.98 ns** | **3.6621** |  **63.35 KB** |
