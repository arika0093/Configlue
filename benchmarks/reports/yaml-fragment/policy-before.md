```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core i7-14700F 2.10GHz, 1 CPU, 28 logical and 20 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  Job-IJPESX : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

IterationCount=5  LaunchCount=1  WarmupCount=3  

```
| Method            | Mean     | Error     | StdDev    | Gen0   | Gen1   | Allocated |
|------------------ |---------:|----------:|----------:|-------:|-------:|----------:|
| ReadChangedPolicy | 6.743 μs | 0.2916 μs | 0.0451 μs | 1.3351 | 0.0153 |  22.58 KB |
