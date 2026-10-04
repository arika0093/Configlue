# Performance optimization audit

The objective is to continue measuring and improving execution time and managed allocations until the relevant paths have been audited and no further worthwhile, behavior-preserving candidate remains. Passing one benchmark group is not completion. The working branch is `local/perf-measure-loop`; the initial parent is `b00f5770`. The isolated worktree keeps unrelated main-branch changes out of measurements.

## Round 1: resolver logging and validation

Same-machine BenchmarkDotNet 0.15.8 ShortRun on Windows 11, Intel Core i7-14700F, .NET 10.0.12; one launch, three warmups, three measurement iterations. Baseline and candidate use identical benchmark source. Logs and full reports are retained in ignored `artifacts/perf-round1/before` and `after` directories.

| Path | Before mean | After mean | Before allocation | After allocation |
| --- | ---: | ---: | ---: | ---: |
| Resolver, 1 source, disabled logger | 219.6 ns | 188.1 ns | 312 B | 168 B |
| Resolver, 4 sources, disabled logger | 698.0 ns | 438.6 ns | 1,248 B | 384 B |
| Resolver, 16 sources, disabled logger | 2,579.6 ns | 1,497.3 ns | 5,072 B | 1,328 B |
| Resolver, 16 sources, enabled counting logger | 2,538.5 ns | 1,660.3 ns | 5,072 B | 1,328 B |
| Contribution validation, annotations disabled | 10.400 ns | 0.124 ns | 88 B | 0 B |
| Pruning, annotations disabled | 5.308 ns | 0.803 ns | 32 B | 0 B |
| Resolved validation, no attributes, annotations enabled | 8.035 ns | 4.888 ns | 24 B | 0 B |
| Contribution validation, no attributes, annotations enabled | 60.223 ns | 62.482 ns | 304 B | 88 B |
| Pruning, no attributes, annotations enabled | 58.049 ns | 2.532 ns | 280 B | 0 B |
| Runtime read with annotation/custom validation | 1,444.4 ns | 1,461.1 ns | 2,416 B | 2,296 B |

Sub-nanosecond no-op results are at the harness measurement floor, not usable latency promises. ShortRun intervals are wide for some methods; allocation changes and their deterministic regression tests are stronger evidence. The runtime validated read does not show a timing improvement, despite reducing allocation. Unlogged resolver allocation remains unchanged at 168/384/1,328 B for 1/4/16 sources; the after 1- and 16-source unlogged means increase slightly (185.7 to 192.7 ns and 1,481.1 to 1,532.3 ns), while the 4-source mean decreases from 429.7 to 425.2 ns.

Resolver logging uses cached, strongly typed `LoggerMessage.Define` delegates, retaining event IDs, templates, severity, structured fields, and dynamic log-level checks. The enabled benchmark uses a counting sink without formatting or I/O so provider costs cannot hide argument allocations. Real logging providers may allocate to render or retain the state. Validation avoids empty lists and model construction when disabled, checks warmed metadata caches before creating value factories, and isolates the capturing exception formatter from successful contribution validation. Pruning without applicable attributes returns the original fragment.

Regression tests verify changing log thresholds after construction, fallback severity/fields, original exception propagation, logger allocation parity against an unlogged resolver, zero-allocation no-op validation, and fragment identity. A new allocation test caught a residual 24 B/operation closure in the failure-only formatter; the formatter was moved to a separate failure-only method and the budget now passes. Final Release net10.0 verification passes 1,642 tests, with zero failures and 17 skipped external-service tests, using one parallel test. Core builds successfully for net10.0, netstandard2.0, and netstandard2.1 with zero warnings or errors. CSharpier checks all seven changed C# files, and `git diff --check` passes.

Reproduce:

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*ResolverLoggingBenchmarks*' '*ValidationPipelineBenchmarks*' '*ReadValidationBenchmarks*' --job short
dotnet test tests/Configlue.Tests/Configlue.Tests.csproj -c Release -f net10.0 -- --maximum-parallel-tests 1
dotnet build src/basic/Configlue.Core/Configlue.Core.csproj -c Release
```

## Round 2: unchanged resolver snapshots

The same ShortRun configuration and unchanged benchmark inputs are retained in `artifacts/perf-round2/before` and `after`.

| Path | Before mean | After mean | Before allocation | After allocation |
| --- | ---: | ---: | ---: | ---: |
| Stable resolver, 1 source | 181.224 ns | 169.156 ns | 168 B | 24 B |
| Stable resolver, 2 sources | 267.432 ns | 243.407 ns | 288 B | 48 B |
| Stable resolver, 4 sources | 433.527 ns | 383.676 ns | 384 B | 96 B |
| Stable resolver, 16 sources | 1,489.964 ns | 1,286.290 ns | 1,328 B | 384 B |
| Subject resolver, 1 source | 643.7 ns | 620.3 ns | 2,848 B | 2,704 B |
| Subject resolver, 4 sources | 1,605.9 ns | 1,495.4 ns | 7,888 B | 7,600 B |
| Subject resolver, 16 sources | 5,007.6 ns | 5,226.0 ns | 28,129 B | 27,185 B |

Every source is still read on every operation. Only the immutable resolution, revision vector, and watch observations are reused when routing, active source, revisions, and nested vector references match. Fresh values/statuses are returned. Changed snapshots own their arrays; escaped old revision/watch snapshots remain unchanged. Subject residency updates and eviction hooks still run on every successful resolution publication. The remaining 24 B per source in the stable benchmark is source-reader work; the warmed resolver with cached reader results allocates exactly 0 B for 1/4/16 sources in regression tests. Standalone revision-vector construction remains an allocation control, not an optimized operation. The 16-source subject timing does not improve and has a wide interval; subject key recomputation remains a measured candidate.

Eight new semantic cases cover fresh values, all-source reads, changed/nested/removed revisions, nested metadata from missing sources, failover, status changes, and retained watch immutability. Three exact allocation budgets cover stable reads.

## Remaining audit

These are outstanding, not claims of saturation:

- Resolver: unchanged revision/watch snapshots now reuse immutable state. Audit changed-revision and cold costs, subject key construction and context equality, and reader allocations. Preserve fresh source reads, failover, immutable escaped snapshots, and subject residency/eviction semantics.
- JSON: plain schema-bearing object serialization still sorts and checks properties every call. Measure this path separately from generated-fragment serialization; cache only immutable metadata and preserve callbacks, polymorphism, converters, ordering, and all serializer options.
- Runtime reads, validation, diagnostics, and watch notification: sample disabled, snapshot, history, listener, and telemetry modes; check for internal use of materializing public revision views.
- Generated fragments: audit merge, collection merge, clone/diff/equality, sparse routing, nested paths, and member lookup against existing benchmarks and tests.
- Write paths: audit routed saves, batch plans, composite edits, serialized writes, and file backup/locking overhead.
- Sources and codecs: audit environment, command line, JSON/JSONC sections, YAML, XML, MessagePack, and schema metadata decode.
- Resources and transformers: audit file reads/writes, stream fingerprinting, S3 streaming, Redis identities/reads/writes, AES, compression, mixed transformer pipelines, and large payloads. Separate storage/network/OS-cache variability from managed hot-path costs.
- Final audit: establish coverage with actual report/test evidence, identify remaining costs as necessary work or unresolved candidates, integrate the verified changes with current main, and rerun relevant checks. Keep the goal active while any candidate or material coverage gap remains.
`nRound 2 final verification: Release net10.0 passes 1,653 tests, zero failures, 17 external-service skips. Core builds all three target frameworks with zero warnings/errors. Formatting and whitespace checks pass.
