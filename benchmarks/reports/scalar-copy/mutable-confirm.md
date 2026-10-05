```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-TGBHRT : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=10  LaunchCount=1  WarmupCount=5  

```
| Method   | Count | Mean      | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|--------- |------ |----------:|----------:|----------:|-------:|-------:|----------:|
| **Model**    | **16**    |  **1.266 μs** | **0.0671 μs** | **0.0351 μs** | **0.2575** | **0.0019** |   **4.36 KB** |
| Fragment | 16    |  1.281 μs | 0.0788 μs | 0.0521 μs | 0.2499 | 0.0019 |   4.22 KB |
| **Model**    | **256**   | **17.777 μs** | **0.7726 μs** | **0.5111 μs** | **2.7466** | **0.3662** |  **46.43 KB** |
| Fragment | 256   | 17.212 μs | 0.4240 μs | 0.2523 μs | 2.7466 | 0.3357 |  46.29 KB |
