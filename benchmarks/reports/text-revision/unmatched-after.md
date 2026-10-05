```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5  

```
| Method            | AssignmentCount | RepeatedOrigin | Mean        | Error       | StdDev      | Gen0   | Gen1   | Allocated |
|------------------ |---------------- |--------------- |------------:|------------:|------------:|-------:|-------:|----------:|
| **UnmatchedRevision** | **0**               | **False**          |    **308.4 ns** |    **23.89 ns** |    **15.80 ns** | **0.0544** |      **-** |     **944 B** |
| **UnmatchedRevision** | **0**               | **True**           |    **322.9 ns** |    **16.53 ns** |     **9.84 ns** | **0.0544** |      **-** |     **944 B** |
| **UnmatchedRevision** | **1**               | **False**          |    **527.7 ns** |    **19.01 ns** |    **11.31 ns** | **0.0677** |      **-** |    **1184 B** |
| **UnmatchedRevision** | **1**               | **True**           |    **520.7 ns** |    **37.37 ns** |    **24.72 ns** | **0.0677** |      **-** |    **1184 B** |
| **UnmatchedRevision** | **16**              | **False**          |  **3,196.3 ns** |    **50.60 ns** |    **30.11 ns** | **0.1984** |      **-** |    **3424 B** |
| **UnmatchedRevision** | **16**              | **True**           |  **3,066.5 ns** |    **90.06 ns** |    **53.59 ns** | **0.1984** |      **-** |    **3424 B** |
| **UnmatchedRevision** | **256**             | **False**          | **48,818.8 ns** |   **475.23 ns** |   **314.34 ns** | **2.2583** | **0.0610** |   **39041 B** |
| **UnmatchedRevision** | **256**             | **True**           | **47,595.9 ns** | **7,582.55 ns** | **4,512.25 ns** | **2.2583** | **0.0610** |   **39041 B** |
