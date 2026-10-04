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

## Round 3: subject identity

Reports are retained in `artifacts/perf-round3/before` and `after`. Resolver before values are round 2 after values (same code and inputs).

| Path | Before mean | After mean | Before allocation | After allocation |
| --- | ---: | ---: | ---: | ---: |
| Single-segment subject key | 68.10 ns | 45.747 ns | 536 B | 288 B |
| Same-subject context equality | 142.88 ns | 0.325 ns | 1,072 B | 0 B |
| Equivalent distinct-subject context equality | 138.20 ns | 94.840 ns | 1,072 B | 576 B |
| Subject resolver, 1 source | 620.3 ns | 429.7 ns | 2,704 B | 888 B |
| Subject resolver, 4 sources | 1,495.4 ns | 827.7 ns | 7,600 B | 1,824 B |
| Subject resolver, 16 sources | 5,226.0 ns | 2,363.2 ns | 27,185 B | 5,568 B |

Single-segment key construction avoids the params array and StringBuilder; canonical normalization, strict UTF-8, base64url encoding, length prefix, and validation are retained. Context equality short-circuits identical subject references, including canonical default subjects; distinct subject instances/types still compare their logical keys. Hashing is unchanged. Equality still checks model, resource, and route fields. The near-zero same-context timing is a trivial comparison at the harness floor. Multiple-segment construction allocation is unchanged at 760 B; its mean changes from 175.70 to 194.66 ns with overlapping wide ShortRun intervals, so no timing improvement is claimed there.

Tests cover all base64 padding lengths, separators, composed/decomposed Unicode, Japanese/emoji, URL substitutions, a large key, invalid/null/whitespace/surrogate input, key getter counts, and existing cross-type/hash identity behavior. Release net10.0 passes 1,666 tests, zero failures, 17 external-service skips. Abstraction builds net10.0/netstandard2.0/netstandard2.1 with zero warnings/errors. Formatting and whitespace checks pass.

## Round 4: read-result status validation

Reports are retained in `artifacts/perf-round4/before` and `after`. Resolver before values are round 3 after values.

| Path | Before mean | After mean | Before allocation | After allocation |
| --- | ---: | ---: | ---: | ---: |
| State success factory | 9.122 ns | 0.225 ns | 24 B | 0 B |
| State not-found factory | 8.606 ns | 0.239 ns | 24 B | 0 B |
| Resource success factory | 11.954 ns | 0.015 ns | 24 B | 0 B |
| Resource not-found factory | 8.646 ns | 0.007 ns | 24 B | 0 B |
| Default resolver, 1 source | 171.2 ns | 166.0 ns | 24 B | 0 B |
| Default resolver, 4 sources | 382.7 ns | 387.0 ns | 96 B | 0 B |
| Default resolver, 16 sources | 1,311.7 ns | 1,226.6 ns | 384 B | 0 B |
| Subject resolver, 16 sources | 2,363.2 ns | 2,332.2 ns | 5,568 B | 5,184 B |

`Enum.IsDefined(Type, object)` boxed every read status. Both typed-state and byte-resource results now validate with explicit enum patterns, preserving rejection of undefined statuses and all schema/value/content checks across all target frameworks. The tiny factory timings are at the harness floor and can benefit from constant folding; the meaningful evidence is zero allocation and the full resolver measurements. The four-source mean does not improve. One deterministic budget exercises all eight public factories, retaining the final payload/status. Four invalid-status tests cover negative, first out-of-range, and integer extremes, retaining the exception parameter name. Release net10.0 passes 1,671 tests, zero failures, 17 external-service skips. Abstraction builds all three target frameworks with zero warnings/errors; formatting/whitespace checks pass.

## Round 5: immutable JSON property plans

The new `JsonSimpleObjectWriteBenchmarks` exercises a plain four-property object with schema metadata, explicit/tied property ordering, and a collection property. Its baseline is the round 4 after report (before this JSON change), retained in `artifacts/perf-round4/after`; candidate reports are in `artifacts/perf-round5/after`.

| Path | Before mean | After mean | Before allocation | After allocation |
| --- | ---: | ---: | ---: | ---: |
| Plain schema-bearing Simple object write | 334.7 ns | 255.9 ns | 528 B | 184 B |

A ConditionalWeakTable keyed by immutable JsonTypeInfo retains eligible sorted property arrays and ineligible plans. Both metadata and serializer options must be read-only. Mutable metadata is inspected and sorted each time. Values, getters, and ShouldSerialize predicates still run on every write. Serializer-option eligibility, callbacks, generated converter payload writers, polymorphism, custom converters, extension data, reference handling, number handling, and default ignore rules retain their existing fallback paths. Stable sorting retains original metadata order for ties; property names are written with the existing writer/options behavior. Weak keys avoid globally retaining transient metadata/options.

Three new tests verify repeated cached writes with changed values and ShouldSerialize outcomes, mutable property order/name/eligibility changes, and repeated fallback for frozen ineligible metadata. Existing JSON callback, converter, AOT metadata, and layout tests pass. Release net10.0 passes 1,674 tests, zero failures, 17 external-service skips. The JSON provider builds all three target frameworks with zero warnings/errors. Formatting/whitespace checks pass. Six existing layout benchmarks also executed successfully; their candidate-only reports establish coverage, not before/after performance claims.

## Remaining audit

These are outstanding, not claims of saturation:

- Resolver: unchanged revision/watch snapshots now reuse immutable state. Audit changed-revision and cold costs, subject key construction and context equality, and reader allocations. Preserve fresh source reads, failover, immutable escaped snapshots, and subject residency/eviction semantics.
- JSON: immutable plain schema-bearing object metadata is now cached. Continue auditing envelope/section reads and fallback serialization allocations, including mutable metadata and cold setup costs. Preserve callbacks, polymorphism, converters, ordering, and all serializer options.
- Runtime reads, validation, diagnostics, and watch notification: sample disabled, snapshot, history, listener, and telemetry modes; check for internal use of materializing public revision views.
- Generated fragments: audit merge, collection merge, clone/diff/equality, sparse routing, nested paths, and member lookup against existing benchmarks and tests.
- Write paths: audit routed saves, batch plans, composite edits, serialized writes, and file backup/locking overhead.
- Sources and codecs: audit environment, command line, JSON/JSONC sections, YAML, XML, MessagePack, and schema metadata decode.
- Resources and transformers: audit file reads/writes, stream fingerprinting, S3 streaming, Redis identities/reads/writes, AES, compression, mixed transformer pipelines, and large payloads. Separate storage/network/OS-cache variability from managed hot-path costs.
- Final audit: establish coverage with actual report/test evidence, identify remaining costs as necessary work or unresolved candidates, integrate the verified changes with current main, and rerun relevant checks. Keep the goal active while any candidate or material coverage gap remains.
Round 2 final verification: Release net10.0 passes 1,653 tests, zero failures, 17 external-service skips. Core builds all three target frameworks with zero warnings/errors. Formatting and whitespace checks pass.
