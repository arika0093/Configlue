#if NET10_0_OR_GREATER
namespace Configlue.Tests;

/// <summary>
/// Deterministic allocation budgets for material hot-path contracts (issue #276).
/// These are coarse invariants measured with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> after warm-up, not exact
/// byte counts: they fail only if an implementation regresses to a materializing
/// shape (hundreds of entries, per-chunk buffers, per-read dictionaries).
/// Exact zero-allocation assertions for nanosecond-scale paths were removed:
/// they coupled production complexity to incidental JIT/allocator behavior
/// without moving the simple-settings product budgets. BenchmarkDotNet groups in
/// <c>benchmarks/Configlue.Benchmarks</c> (notably
/// <c>SingleFileSettingsBenchmarks</c>) remain the investigative and
/// decision-making metric.
/// </summary>
// Allocation measurements share the process GC and ArrayPool caches.
[NotInParallel]
public sealed class AllocationBudgetTests
{
    [Test]
    public void DictionaryEquality_DoesNotMaterializeEntries()
    {
        var smallLeft = CreateLookup(8);
        var smallRight = CreateLookup(8);
        var largeLeft = CreateLookup(512);
        var largeRight = CreateLookup(512);

        var equal = false;
        var smallAllocated = Measure(() =>
        {
            equal = ConfiglueValueComparer.AreEqual(smallLeft, smallRight);
        });
        equal.ShouldBeTrue();
        var largeAllocated = Measure(() =>
        {
            equal = ConfiglueValueComparer.AreEqual(largeLeft, largeRight);
        });
        equal.ShouldBeTrue();

        // A per-entry ToArray/ToDictionary materialization (issue #221) would add
        // ~500 entries worth of allocations here; native TryGetValue lookup stays flat.
        // The budget is deliberately coarse (per #214): allocator alignment and
        // per-call fixed costs vary by platform (e.g. arm64), while a materialization
        // regression would exceed it by an order of magnitude.
        (largeAllocated - smallAllocated).ShouldBeLessThanOrEqualTo(16 * 1024);
    }

    private static Dictionary<string, string> CreateLookup(int count) =>
        Enumerable
            .Range(0, count)
            .ToDictionary(static index => $"key-{index}", static index => $"value-{index}");

    [Test]
    public void SingleSourceRevisionVector_ConstructionAndLookup_HasFixedSmallBudget()
    {
        var revision = new StateRevision(SourceId.From("source"), "revision-1");
        var missing = SourceId.From("missing");

        // One small dictionary-backed vector per construction (~288 bytes on
        // net10.0/x64 for the dictionary, its entries, and the read-only
        // wrapper); the lookups themselves allocate nothing. The bound stays
        // coarse so allocator alignment differences across platforms do not
        // turn it into trivia. Outcomes are recorded and asserted outside
        // the measured region because assertion helpers allocate on the calling
        // thread.
        var hit = false;
        var miss = true;
        var allocated = Measure(() =>
        {
            var vector = StateRevisionVector.FromSingle(revision);
            hit = vector.TryGetRevision(revision.SourceId, out _);
            miss = vector.TryGetRevision(missing, out _);
        });

        hit.ShouldBeTrue();
        miss.ShouldBeFalse();
        allocated.ShouldBeLessThanOrEqualTo(384 * Iterations);
    }

    [Test]
    public async Task PipelineFingerprint_AllocationDoesNotGrowWithChunkCount()
    {
        const int payloadSize = 32 * 1024;
        var payload = new byte[payloadSize];
        new Random(42).NextBytes(payload);

        // Minimum of several runs to smooth threadpool/pipe-scheduling noise.
        var oneChunk = long.MaxValue;
        var manyChunks = long.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            oneChunk = Math.Min(oneChunk, await MeasureFingerprintAsync(payload, payloadSize));
            manyChunks = Math.Min(manyChunks, await MeasureFingerprintAsync(payload, 1024));
        }

        // A per-chunk byte[] allocation (issue #173) would add ~payloadSize here.
        (manyChunks - oneChunk).ShouldBeLessThanOrEqualTo(8 * 1024);
    }

    private const int Iterations = 1000;

    private static long Measure(Action action)
    {
        // Warm the complete loop so generic caches and tiered compilation have
        // the same opportunity to settle for small and large inputs.
        for (var index = 0; index < Iterations; index++)
        {
            action();
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Iterations; index++)
        {
            action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static async Task<long> MeasureFingerprintAsync(byte[] payload, int chunkSize)
    {
        await ReadAndFingerprintAsync(payload, chunkSize);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        var before = GC.GetAllocatedBytesForCurrentThread();
        await ReadAndFingerprintAsync(payload, chunkSize);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static async Task ReadAndFingerprintAsync(byte[] payload, int chunkSize)
    {
        await using var result = PipelineResourceReader.FromStream(
            new ChunkedMemoryStream(payload, chunkSize),
            contentFingerprintCompleted: static _ => { }
        );
        _ = await result.ReadAllAsync().ConfigureAwait(false);
    }

    private sealed class ChunkedMemoryStream(byte[] content, int chunkSize)
        : MemoryStream(content, writable: false)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
#endif
