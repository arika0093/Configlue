# Issue #271: resolver subject-cache measurements

In-test coarse measurements backing the idle-only eviction decision
(`StateSourceResolver<T>`, single in-memory source, `AppSettings.Fragment`).
Method: `GC.GetAllocatedBytesForCurrentThread()` after warm-up plus
`Stopwatch` around `ReadAsync` / `GetSourcesForWatch` /
`EvictIdleSubjectResolutionsForTest()`. These are order-of-magnitude
engineering numbers, not BenchmarkDotNet results and not CI thresholds.
Do not compare across machines or runtimes.

Environment: Windows 10.0.26200 X64, .NET 10.0.12, Release net10.0,
`Stopwatch.Frequency = 10,000,000`. Two runs on the same machine:

| Case | Run 1 | Run 2 |
| --- | --- | --- |
| Default read (warm, same resolver) | ~0.5 µs/op, ~120 B/op | ~0.7 µs/op, ~120 B/op |
| Subject read (warm, same subject) | ~2.4 µs/op, ~990 B/op | ~3.1 µs/op, ~990 B/op |
| Subject read (cold, new subject each read) | ~5.3 µs/op, ~2.1 KB/op | ~6.5 µs/op, ~2.1 KB/op |
| Watch capture (`GetSourcesForWatch`, 1 route) | ~0.9 µs/op, ~440 B/op | ~1.7 µs/op, ~440 B/op |
| 256 subjects retained | 256 entries, ~490 KiB total (~1.9 KB/entry) | 256 entries, ~498 KiB total (~2.0 KB/entry) |
| Sweep rescan, 256 entries, nothing idle (capacity-trigger analogue) | ~0.9 ms, evicts 0 | ~1.1 ms, evicts 0 |
| Sweep, 256 entries, all idle | ~0.45 ms, evicts 256 | ~0.52 ms, evicts 256 |

## Intermediate shape and removal decision

The pre-#271 resolver had a capacity trigger
(`SubjectResolutionSweepThreshold = 256`, amortized so a scan ran at most
once per 256 reads at capacity) in addition to the idle-timeout sweep.
Measured on the same 256-entry set, the capacity scan costs ~1 ms yet
evicts 0 entries whenever the global idle timeout has not elapsed: every
entry it would remove is removed anyway by the next idle sweep, at most
one idle timeout later. Keeping the trigger therefore adds a periodic
~1 ms rescan (roughly ~4 µs amortized per read at capacity) with no
eviction benefit, plus the countdown/limit plumbing on the hot path.
The #271 simplification removes the capacity trigger and keeps idle-only
eviction: the hot path is a single timestamp comparison, and a sweep runs
at most once per idle timeout.

## Burst bound (documented acceptance)

Within one idle window the cache grows with the number of distinct
subjects touched: there is no per-window cap by design. The bound is

> live entries ≤ distinct (subject, route) pairs read within the last idle timeout.

At ~2 KB per entry (table above: resolution + single-route topology +
revision vector + key), 256 live subjects cost ~0.5 MiB transiently.
With the default 5-minute timeout the worst case is
(request rate × 300 s) distinct subjects; the entries are small immutable
snapshots, eviction is opportunistic, and a watch that races eviction
falls back to the conservative full source list, so an oversized window
costs only transient memory, never correctness. Operators that need a
tighter bound should shorten `subjectResolutionIdleTimeout` per resolver
instead of re-adding a capacity rescan.

## Coverage

- `StateSourceResolverCacheTests.SameWindowManySubjects_AreBoundedByDistinctSubjectsInWindow`
  pins the burst shape: 256 subjects read in the same window are all
  retained (~0.5 MiB), then one read past the idle timeout sweeps back to 1.
- `StateSourceResolverConcurrencyTests` pins the racy paths the
  simplification relies on: concurrent same-key updates are last-writer-wins
  (single atomic indexer assignment), and update/evict races stay consistent
  via pair-remove (stale snapshots never evict the fresh resident entry).
