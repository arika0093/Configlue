# Runtime coordinators (internal architecture note)

`ConfiglueRuntime<TModel, TFragment>` (`src/basic/Configlue.Core/Options/ConfiglueRuntime.cs`)
is a **facade/orchestrator**. It owns no read/write/watch/migration/subject state
itself. All of that lives in directly-owned, `internal`, concrete coordinators under
`src/basic/Configlue.Core/Options/Runtime/`. There is no internal DI container or
service graph: the facade constructs every coordinator in its constructor and
delegates to it. New files are preferred over new partials so each scope stays
reviewable and testable in isolation.

## Component map

| Coordinator | Owns (state + invariants) | Must NOT grow into |
|---|---|---|
| `RuntimeLifetime` | The single shared gate, disposed flag, active-operation count, operations-drained signal. Registration-vs-shutdown atomicity. | Listener/watch lists (they only *use* the gate). |
| `RuntimeSubjectContext` | The ambient `AsyncLocal` subject, scope enter/restore, subject-to-resource-context derivation. | Anything that stores per-subject data. |
| `RuntimeSourceTopology<TFragment>` | Registered set, active (non-retired) snapshot, retired IDs + gate, topology-change signal, details source keys, single-source fast-path snapshot. | Read/merge logic, write plans. |
| `RuntimeResolutionEngine<TModel, TFragment>` | Model-defaults contribution, schema-migration chain, read-validation mode, layered read/merge, revision vectors. | Writes, watchers, sessions. |
| `RuntimeValidationPipeline<TModel, TFragment>` | Validators, data-annotations opt-in, member/model validation caches. Pure in/out; takes fragments explicitly. | Resolution or routing state. |
| `RuntimeMigrationCoordinator<TModel, TFragment>` | Storage-migration orchestration (source-to-source, sources-to-targets, retirement verification). No mutable state of its own. | Fragment migration (that is the engine's `MigrateFragmentAsync`). |
| `RuntimeWriteCoordinator<TModel, TFragment>` | Bound `StateWritePlan`, default-source inference, routing/merge-aware planning, patch application, previews, write diagnostics. | Resolution snapshots (passed in as `ResolvedState<TModel, TFragment>`). |
| `RuntimeWatchCoordinator<TModel, TFragment>` | Listener lists, shared watch loop, per-subject watcher table + barrier, debounce, wait-task scratch. | Operation counting (lifetime's). |
| `RuntimeInspectionCoordinator<TModel, TFragment>` | Check, details/snapshot building, static diagnostics. Composes others; owns no mutable state. | Any new stored state. |
| `RuntimeEditSessionCoordinator<TModel, TFragment>` | Conflict-resolution mode, session open/save/rebase, contract-test snapshot barrier. | Write execution (delegates to the write coordinator). |
| `RuntimeModel<TModel, TFragment>` / `RuntimeState` / `RuntimeModelCloner<TModel, TFragment>` | Stateless generated-operation fast paths, pure revision/topology/conflict helpers, clone policy. | Mutable state of any kind. |
| `RuntimeDiagnosticRecorder` (pre-existing) | All diagnostic events/snapshots/listeners. Already the diagnostics owner; kept as-is. | — |

Cross-coordinator data uses narrow internal types: `ResolvedState<TModel, TFragment>`,
`ResolvedContribution<TFragment>`, `ResolvedFailure<TFragment>`, `ResolvedSourceProbe<TFragment>`
(engine output consumed by writes/inspection/edits).

## Where new behavior belongs

- New read/merge/fallback semantics → `RuntimeResolutionEngine`.
- New validation rules or modes → `RuntimeValidationPipeline`.
- New write routing, batching, or preview behavior → `RuntimeWriteCoordinator`.
- New watcher, debounce, or notification behavior → `RuntimeWatchCoordinator`.
- New subject scoping → `RuntimeSubjectContext` (+ thin facade surface).
- New session/commit/rebase behavior → `RuntimeEditSessionCoordinator`.
- New health/details content → `RuntimeInspectionCoordinator`.
- New lifetime/disposal rules → `RuntimeLifetime`.
- Shared pure helpers only → `RuntimeState` / `RuntimeModel<TModel, TFragment>`.

Do NOT accumulate new fields, locks, or responsibilities back into
`ConfiglueRuntime`. Public capability surface stays on the facade and delegates.

## Hard constraints (from #227, preserved by this split)

- No new public API. All coordinators are `internal`; public-API approval tests
  (`Configlue.Tests.PublicApi`) must stay green.
- No new interface dispatch or per-operation delegate allocations on hot paths.
  Coordinators are `sealed` concrete classes held by reference; collaborators are
  constructor-injected once. The generated model-operation delegates
  (`ToFragment`/`Diff`/`FromFragment`) and `ArrayPool` scratch pooling are untouched.
- Move concurrency primitives with the state they protect. The only shared lock
  is the lifetime gate, used through narrow methods (`Register`, `Unregister`,
  `ExecuteUnderGate`, `TrySnapshot`, `TryBeginShutdown`).
- NativeAOT/source-generated paths preserved: no new reflection; the
  data-annotations reflection paths keep their existing dynamic-code guards.
- Formatting is enforced by CSharpier on build; run `dotnet csharpier format`
  on touched files.

## Verification for this refactor

- `dotnet build Configlue.slnx -c Release`: 0 errors.
- Full test suite green, including new direct component tests
  (`tests/Configlue.Tests/State/RuntimeCoordinatorTests.cs`) covering lifetime
  drain/idempotent shutdown, subject scoping, topology retire/fast-path,
  validation accept/reject, and isolated engine resolution.
- Benchmarks project builds. Short-run reference numbers on this machine
  (i7-14700F, .NET 10, `ShortRun` job), after-state:
  - Warm single-source `GetValueAsync`: ~866 ns / 512 B; facade variant ~875 ns / 512 B.
  - Watched notification (`PublishChangeAsync`): ~7.26 us / 3757 B.
  - Layered resolver reads (untouched #225 component): ~1.4 us (1 source),
    ~1.9 us (2), ~2.8 us (4), ~8.1 us (16).
  - Hot-path allocation parity holds by construction: identical rents,
    identical generated delegates, identical diagnostics fast path.
  - Note: `SaveRoutingBenchmarks` setup throws `InvalidOperationException`
    (multiple writable root sources, no default owner) identically before and
    after this change — pre-existing benchmark setup issue, unrelated to #227.
