# Benchmarks

The ongoing measurement and optimization audit is recorded in [performance-audit.md](performance-audit.md), including same-machine results, validation, and outstanding coverage.

`CollectionCloneBenchmarks` isolates generated fragment deep cloning for lists, sets,
dictionaries, and a combined collection model at 0, 16, and 4,096 elements. It checks
structural equality in setup and measures allocations without storage or resolver costs.

Run every benchmark in Release mode:

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks
```

To run one group, pass a BenchmarkDotNet filter:

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*FilePersistenceBenchmarks*'
```

`OptionsRuntimeBenchmarks` measures warm reads, cached facade and `IOptionsMonitor<T>` values, and a watched in-memory source update through listener notification. `LayeredResolutionBenchmarks` measures configuration resolution with 1, 4, and 16 in-memory sources. `StateSourceResolverBenchmarks` measures the source resolver scanning 1, 4, and 16 sources before finding a value. `FilePersistenceBenchmarks` compares file-backed reads and async saves between Configlue and Configuration.Writable using separate JSON files, each library's default document format, and default backup behavior. `FileResourceReadBenchmarks` and `FileResourceWriteBenchmarks` measure raw resource I/O at 1 KiB, 64 KiB, and 1 MiB without backup rotation. `SerializedFileReadBenchmarks` compares pipeline-preferred and memory-fallback file reads with JSON decoding at the same sizes. `HttpStreamingBenchmarks` and `S3StreamingBenchmarks` compare buffered vs pipeline large-payload reads with JSON decoding at 256 KiB and 2 MiB, without external network access: HTTP serves a reusable backing payload through a custom `HttpMessageHandler` returning `StreamContent`, S3 through an in-memory `IS3ObjectClient`/`IS3ObjectStreamClient` fake (buffered `GetObjectAsync` copies into a response-sized array, streaming `GetObjectStreamAsync` exposes the shared backing data). Both decode the same payload via `SerializedStateReader` with a pipeline-capable `JsonStateCodec` (`UseAsyncStreamDecoding`) against a memory-only fallback forcing `ReadAsync`. Payload bytes are built once in `GlobalSetup` and reused, and response/stream disposal completes inside every iteration.

`SingleFileSettingsBenchmarks.cs` (issue #231) is the dedicated simple-settings group for the zero-ceremony path (`Add<T>().UseLocalJson(path)`). It measures cold initialization plus first read, warm read, small patch save with default backup behavior, watched reload through `OnChange`, and allocations, against direct `System.Text.Json` plus `File` baselines and Microsoft configuration binding for reads. The overhead budget is stated in the class remarks: warm read at most 3x the direct deserialize baseline, patch save at most 3x the direct serialize-plus-write baseline, cold init bounded by one file read plus one-time model setup, and watched reload dominated by notification plus debounce rather than resolver work. Run it with `--filter '*SingleFileSettingsBenchmarks*'`.

`OptimizationBenchmarks.cs` adds targeted groups used to choose and verify performance work: `ReadValidationBenchmarks` (validation on/off), `LayeredResolutionFallbackBenchmarks` (1/2/4/16 sources, first-source success vs deep fallback), `FragmentMergeBenchmarks` (fragment merge with 1 vs all members present), `NestedModelReadBenchmarks`, `CollectionMergeBenchmarks` (`Append` vs `SetUnion` with 2 and 8 sources), `SaveRoutingBenchmarks` (single-source vs multi-source routed save), `JsonCodecLayoutBenchmarks` (`DocumentLayout.Simple` vs `Detailed` serialize/deserialize), `JsonSectionBenchmarks` (JSONC section read/write), `EnvironmentSourceBenchmarks`, `CommandLineSourceBenchmarks`, and `FileBackupBenchmarks` (backup generations 1/3/10). All groups use `MemoryDiagnoser`.

`AllocationHotPathBenchmarks.cs` measures AES, compression, combined transformer output, streamed fingerprinting, schema-bearing MessagePack/JSON serialization/decoding, and Redis identity construction. Payload groups use 100 B, 4 KiB, and 64 KiB where relevant. `StateRevisionVectorBenchmarks` measures construction, internal lookup, and public dictionary-view access for 0, 1, 2, 4, and 16 entries. `LayeredResolutionFallbackBenchmarks` exercises 1/2/4/16 source resolution; codec and fragment groups cover JSON and generated-fragment paths. `MemoryDiagnoser` reports allocated bytes and Gen0/Gen1/Gen2 collections for each benchmark.

Issue #210 single-pass metadata decode comparison (same machine, same filter run): `MessagePackCodecAllocationBenchmarks.SplitMetadataThenDeserialize` is the old split-path baseline (`ReadSchemaMetadata` + `Deserialize`, two scans) and `DeserializeWithMetadataSinglePass` calls `MessagePackStateCodec<T>.DeserializeWithMetadata` directly as the production fast path. `JsonCodecMetadataBenchmarks` does the same split-vs-single-pass pair for `JsonStateCodec<T>` with schema-bearing payloads in both `DocumentLayout.Simple` (`$version`) and `DocumentLayout.Detailed` (`$configlue`/`$value` envelope) layouts, so embedded metadata is always present. `MetadataSinglePassReaderBenchmarks.ReadSinglePassAsync` measures the end-to-end `SerializedStateReader<T>` path, which must select the `IStateCodecWithMetadata<T>` single-pass capability; a dispatch regression back to the split fallback shows up as higher allocated bytes and time. Compare the `Allocated` column within each class/layout between `Split*` (baseline) and `*SinglePass*` on the same run; do not compare across machines or runtimes. Third-party codec fallback coverage stays out of scope for this comparison.

`GeneratedWriteRoutingBenchmarks.cs` (issue #211, follow-up of #170) measures the generated
write-routing allocation path that `FragmentMergeBenchmarks` does not cover. It uses a
16-member value-type-heavy generated model (`int`/`bool`/`long`/`double`, enums, nullable
value types, `Guid`/`DateTime`/`TimeSpan` structs, one `string`) and `[Params(1, 4, 16)]`
sparse patches built in setup. All storage I/O is pinned to `InMemoryStateSource<T>` so
routing allocations are not hidden behind I/O. The three benchmarks all run production
routing paths (plain `Merge`/`ToModel` is intentionally not measured here):

- `RoutePatch`: generated `IConfiglueRoutablePatch.Route` partition, no I/O.
- `SaveRoutedAsync`: `IWritableState.SaveAsync` through a two-source routed plan.
- `SaveCompositeAsync`: `IWritableState.SaveAsync` through a two-component
  `CompositeStateSource` (`left`/`right` members split across components) so the
  composite component-patch path runs on the production multi-component topology.

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*GeneratedWriteRoutingBenchmarks*'
```

A fast smoke check that the group sets up and runs (numbers are not publishable):

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*GeneratedWriteRoutingBenchmarks*' --job dry
```

To confirm the benchmarks detect a pre-#170 regression, temporarily route through the
dynamic surface again (for example, implement the measurement with
`patch.EnumeratePresentMembers().ToArray()` plus `object?` member values instead of the
generated `Route`/`EnumeratePresentMembersFast` path, or box value-type members through
`ConfiglueFragmentMember.Value`), re-run the filter above on the same machine, and
compare the `Allocated` column: the reverted run allocates an iterator, a materialized
array, and one box per value-type member on top of the baseline. Restore the generated
path afterwards.

`NestedWriteRoutingBenchmarks220.cs` (issue #220) extends the #211 1/4/16-member
coverage with nested routed paths. It uses a root model with two nested objects
(`Database`/`Cache`, 16 leaves total) and longest-prefix ownership inside each nested
object (the nested root routes to one source while one leaf is overridden to the other
source), so every benchmark recurses through `HasRouteBelow`/`ResolveSourceIdOrNull`
compiled `ConfiglueMemberPath` lookups. The three benchmarks all run production routing
paths with `MemoryDiagnoser` (allocated bytes plus throughput):

- `RouteNestedPatch`: generated `IConfiglueRoutablePatch.Route` partition, no I/O.
- `SaveNestedRoutedAsync`: `IWritableState.SaveAsync` through a two-source
  longest-prefix plan.
- `CommitNestedCompositeAsync`: edit-session commit through a single-`child`
  `CompositeStateSource` (same single-component mirror as #211) so the composite
  `PartitionCompositeChanges` recursion runs; values alternate every iteration so the
  edit always diffs into nested fragment changes.

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*NestedWriteRoutingBenchmarks220*'
`MemberLookupBenchmarks.cs` (issue #222) measures generated member-ID lookup scaling
with 4/16/64/128-member models: centralized `TryGetMember` over all IDs,
`ConfiglueMemberPath` leaf resolution over all IDs, and full-fragment XML/YAML
serialization. `SparseMemberLookupCodecBenchmarks` serializes sparse fragments with
4/16/64/128 present members on the fixed 128-member model. All groups use
`MemoryDiagnoser`. A return to per-member linear scans shows up as quadratic growth in
`Mean` across `MemberCount` (or with `PresentCount` at fixed schema size) instead of
linear growth, and as nonzero `Allocated` on the lookup benchmarks.

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*MemberLookup*'
```

A fast smoke check that the group sets up and runs (numbers are not publishable):

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*NestedWriteRoutingBenchmarks220*' --job dry
```

To confirm the group regresses when string round trips are restored, route the
composite partition through dotted strings again (for example, `path.Add(member.Name)`
plus `string.Join(".", path)` per member with the string
`ResolveWriteComponent`/`HasWriteRouteBelow` overloads, or recompile schema-bound
lookups via `ConfiglueMemberPath.FromNames` per member), re-run the filter above on the
same machine, and compare the `Allocated` column: the reverted run allocates one joined
string per member per nesting level plus one split/name-lookup pass per resolution on
top of the baseline. Restore the generated-ID path afterwards.

dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*MemberLookup*' --job dry
```

Before an allocation optimization, capture its relevant group at the parent revision and again at the candidate revision on the same machine and runtime. Keep the BenchmarkDotNet reports with the review notes; do not treat numbers from different machines or runtime versions as a regression threshold. For a focused comparison:

Issue #165's same-machine before/after allocation results are recorded in [serialized-writer-allocation-results.md](serialized-writer-allocation-results.md). Issue #168's source-count scratch-pooling allocation comparison is recorded in [runtime-scratch-pooling-results.md](runtime-scratch-pooling-results.md).

Issue #166's same-machine before/after schema-bearing JSON allocation results are recorded in [json-codec-schema-allocation-results.md](json-codec-schema-allocation-results.md).

```shell
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*Allocation*'
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*StateRevisionVector*'
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*MessagePackCodecAllocationBenchmarks*,*JsonCodecMetadataBenchmarks*,*MetadataSinglePassReaderBenchmarks*'
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*HttpStreamingBenchmarks*'
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*S3StreamingBenchmarks*'
```

Streaming comparison: every group above uses `MemoryDiagnoser`. Compare `Allocated`/`Alloc Ratio` and Gen0/Gen1/Gen2 for allocation pressure plus `Mean` for throughput. The streaming path avoids the response-sized managed `byte[]` (HTTP `ReadAsByteArrayAsync` copy, S3 buffered copy), so a healthy run shows `Alloc Ratio` well below 1.0x at 256 KiB and 2 MiB. If the streaming path regresses to a response-sized `byte[]`, the allocation delta collapses toward 1.0x.

Deterministic allocation budgets live in `tests/Configlue.Tests/Performance/AllocationBudgetTests.cs`
(net10.0 only) and run in the normal test suite. They use
`GC.GetAllocatedBytesForCurrentThread()` after warm-up with coarse budgets, covering
dictionary-equality without entry materialization, chunk-count-independent pipeline
fingerprinting, and single-source revision-vector construction. Exact zero-allocation
assertions were removed in issue #276: they coupled production complexity to incidental
JIT/allocator behavior without moving the product budgets. Assert outcomes outside the
measured region: assertion helpers allocate on the calling thread.
`SingleFileSettingsBenchmarks` above is the higher-level decision metric for
allocation work, not isolated nanosecond/allocation tests.

```shell
dotnet test tests/Configlue.Tests/Configlue.Tests.csproj -c Release -f net10.0 -- --treenode-filter "/*/*/*AllocationBudgetTests/*"
```

BenchmarkDotNet reports elapsed time and allocated bytes for the benchmark process and runtime. Compare results from the same machine, .NET runtime, power mode, and build configuration. File persistence numbers include local file system and OS cache behavior. Results are measurements, not CI thresholds; the two libraries use different document formats, so file size and serialization work are not identical.

## #214: allocation regression matrix (#164–#175 → benchmark mapping)

Every allocation optimization issue from the #176 review has a benchmark that
would regress if the old behavior returned. `#209`–`#213` extend this table
with transformer split, metadata end-to-end, write-routing, Redis, and HTTP/S3
groups; class names below stay valid when those branches merge.

| Issue | Optimization | Benchmark class → method | What regresses if the old behavior returns |
| --- | --- | --- | --- |
| #164 | Destination/ownership-aware transformer output, no full-buffer alloc per stage | `TransformerAllocationBenchmarks` → `Aes` / `Compression` / `CompressionThenAes`; `MixedTransformerPipelineBenchmarks214` → `PipelineWriteAsync` / `PipelineReadAsync` (1/2/3 stages) | Per-stage buffer copy returns; mixed sync+async `Allocated` rises while the stage-1 sync baseline stays flat |
| #165 | Explicitly owned, poolable serialized-write buffers (normal + batch) | `SerializedWriterAllocationBenchmarks` → `NormalWriteAsync` / `BatchWriteAsync` | Temporary buffer per write; batch path loses ownership |
| #166 | No JSON serialize → temp buffer → DOM parse → reserialize on writes | `JsonCodecLayoutBenchmarks` → `Serialize` / `SerializeWithoutSchema` / `Deserialize`; `JsonMetadataBearingReadBenchmarks214` → `Deserialize` | Extra temp buffer + DOM parse on the write path |
| #167 | Single-pass embedded schema-metadata + value decode | `MessagePackCodecAllocationBenchmarks` → `ReadMetadataAndDecodeValue` vs `Deserialize`; `JsonMetadataBearingReadBenchmarks214` → `DeserializeWithMetadata` vs `ReadSchemaMetadata` + `Deserialize` | Two-pass metadata-then-value decode; the split-vs-single-pass delta collapses |
| #168 | No per-read scratch array/list in normal runtime resolution | `LayeredResolutionFallbackBenchmarks` → `ResolveSourcesAsync` (1/2/4/16); `StateSourceResolverBenchmarks` → `ResolveSourcesAsync` | Allocation grows with source count again |
| #169 | Inline small `StateRevisionVector` + lazy dictionary views | `StateRevisionVectorBenchmarks` → `ConstructAndLookup`; `RevisionVectorViewBenchmarks214` → `LookupHit` / `LookupMiss` / `ViewMaterializeFirstAccess` / `ViewCachedAccess` (0/1/2/4/16) | Dictionary allocated at construction; `Revisions` view materialized eagerly so lookup benchmarks rise |
| #170 | Allocation-friendly generated fragment operations instead of dynamic yield/object path | `FragmentMergeBenchmarks` → `Merge` / `ToModel` | Iterator/object allocations in present-member enumeration |
| #171 | Non-delegate fast paths for `ResourceWriteMutation` and transformed batches | `SerializedWriterAllocationBenchmarks` → `BatchWriteAsync`; `SaveRoutingBenchmarks` → `SaveSingleSourceAsync` / `SaveMultiSourceRoutedAsync` | Delegate/callback allocation per write |
| #172 | Redis read/write and resource-identity allocations | `RedisIdentityAllocationBenchmarks` → `CreateResourceIdentity` (read/write preparation → #212) | Per-operation identity string/allocations |
| #173 | No per-chunk `byte[]` in pipeline fingerprinting | `PipelineFingerprintAllocationBenchmarks` → `ReadAndFingerprintAsync` (ChunkSize 1/128) | Chunk-count-dependent allocation |
| #174 | Streaming pipeline readers for HTTP/S3, no full-response buffering | `FilePersistenceBenchmarks`; `SerializedFileReadBenchmarks` (large-payload buffered vs pipeline → #213) | Full-response buffering on large payloads |
| #175 | Fixed micro-allocations on byte and identity hot paths | `WriteRouteBenchmarks` → `DiagnosticStringLookup` vs `GeneratedIdentityLookup` / `GeneratedRouteBelow`; `RedisIdentityAllocationBenchmarks` | Fixed per-operation small allocations |
| #220 | No per-member dotted-string round trips in generated write routing (nested longest-prefix via compiled member IDs) | `NestedWriteRoutingBenchmarks220` → `RouteNestedPatch` / `SaveNestedRoutedAsync` / `CommitNestedCompositeAsync` (1/4/16 nested leaves) | Joined property-path string per member per level plus split/name re-resolution per lookup; restoring either raises `Allocated` |

Same-machine/ same-runtime comparison procedure:

```shell
git stash -u  # or check out the parent revision in a separate worktree
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*RevisionVectorViewBenchmarks214*'
# ... candidate revision in this worktree, same command, same machine ...
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*MixedTransformerPipelineBenchmarks214*'
dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*JsonMetadataBearingReadBenchmarks214*'
```

Rules: Release configuration, same machine, same .NET runtime, same power
mode; capture the parent-revision group first, then the candidate revision;
keep the BenchmarkDotNet reports with the review notes. Never compare numbers
across machines or runtime versions, and never transcribe BenchmarkDotNet
numbers into unit-test thresholds.

Deterministic allocation budgets in
`tests/Configlue.Tests/Performance/AllocationBudgetTests.cs` (net10.0 only)
are coarse invariants measured with `GC.GetAllocatedBytesForCurrentThread()`
after warm-up: they fail only if an optimization regresses to a materializing
implementation. Exact byte budgets are prohibited because results depend on
runtime and version; BenchmarkDotNet figures must not be copied into unit-test
thresholds.
