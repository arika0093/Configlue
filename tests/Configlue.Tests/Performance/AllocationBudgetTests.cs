#if NET10_0_OR_GREATER
using System.Buffers;
using Configlue.CompilerServices;
using Configlue.Provider.Json;

namespace Configlue.Tests;

/// <summary>
/// Deterministic allocation budgets for the hot paths optimized in issues
/// #164-#176. These are coarse invariants measured with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> after warm-up, not exact
/// byte counts: they fail only if an optimization regresses back to a
/// materializing implementation. BenchmarkDotNet groups in
/// <c>benchmarks/Configlue.Benchmarks</c> provide the fine-grained
/// before/after numbers.
/// </summary>
public sealed class AllocationBudgetTests
{
    [Test]
    public void StripUtf8Bom_SingleSegmentPrefix_AllocatesNothing()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' };
        var withoutBom = new byte[] { (byte)'{', (byte)'}' };
        var withBomSequence = new ReadOnlySequence<byte>(withBom);
        var withoutBomSequence = new ReadOnlySequence<byte>(withoutBom);

        var allocated = Measure(() =>
        {
            _ = JsonStateCodecOperations.StripUtf8Bom(in withBomSequence);
            _ = JsonStateCodecOperations.StripUtf8Bom(in withoutBomSequence);
        });

        allocated.ShouldBe(0);
    }

    [Test]
    public void StripUtf8Bom_SegmentedPrefix_AllocatesNothing()
    {
        // Split the 3-byte BOM across two segments so the slow prefix path runs.
        var first = new TestSequenceSegment(new byte[] { 0xEF });
        var second = first.Append(new byte[] { 0xBB, 0xBF, (byte)'{', (byte)'}' });
        var sequence = new ReadOnlySequence<byte>(first, 0, second, 4);

        // Shouldly builds assertion messages on the calling thread, so record the
        // outcome and assert outside the measured region.
        var strippedLength = 0;
        var allocated = Measure(() =>
        {
            strippedLength = (int)JsonStateCodecOperations.StripUtf8Bom(in sequence).Length;
        });

        strippedLength.ShouldBe(2);
        allocated.ShouldBe(0);
    }

    [Test]
    public void SingleSourceRevisionVector_ConstructionAndLookup_HasFixedSmallBudget()
    {
        var revision = new StateRevision(SourceId.From("source"), "revision-1");
        var missing = SourceId.From("missing");

        // One small vector object (~88 bytes); the lookup itself allocates nothing.
        // Outcomes are recorded and asserted outside the measured region because
        // assertion helpers allocate on the calling thread.
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
        allocated.ShouldBeLessThanOrEqualTo(128 * Iterations);
    }

    [Test]
    public void OrdinalFragmentEnumeration_DoesNotAllocateIterator()
    {
        var fragment = new AppSettings.Fragment
        {
            Label = Optional<string?>.Present("label"),
        };
        fragment.ShouldBeAssignableTo<IConfiglueOrdinalDynamicFragment>();

        // Assertion helpers allocate on the calling thread, so record the
        // traversal outcome and assert outside the measured region.
        var count = 0;
        object? value = null;
        var allocated = Measure(() =>
        {
            count = 0;
            value = null;
            foreach (var member in fragment.EnumeratePresentMembersFast())
            {
                count++;
                value = member.Value;
            }
        });

        count.ShouldBe(1);
        value.ShouldBe("label");
        allocated.ShouldBe(0);
    }

    [Test]
    public void TransformerAsyncCapabilityCheck_DoesNotRescanPerOperation()
    {
        IStateByteTransformer[] transformers = [new SyncTransformer()];

        // The first call populates the cached capability; later calls reuse it.
        var hasAsync = true;
        var allocated = Measure(() =>
        {
            hasAsync = StateByteTransformerPipeline.HasAsync(transformers);
        });

        hasAsync.ShouldBeFalse();
        allocated.ShouldBe(0);
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
        action();
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

    private sealed class SyncTransformer : ISynchronousStateByteTransformer
    {
        public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source) => source;

        public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source) => source;
    }

    private sealed class TestSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public TestSequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new TestSequenceSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };
            Next = segment;
            return segment;
        }
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
