using System.Buffers;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Codecs;
using Configlue.Extensibility;
using Configlue.Provider.Json;
using Configlue.Resources;
using Configlue.State;
using Configlue.Transformers;

// Follow-up #214 for #164: production-pipeline benchmark with no-op sync and
// async transformers mixed in registration order. Stage 1 is sync-only
// (HasAsync fast path, no async machinery); stages 2-3 add an async no-op so
// TransformReadAsync/TransformWriteAsync take the async core path. The async
// no-op completes synchronously, which isolates dispatch, buffer-ownership,
// and state-machine allocation from I/O noise: if the pipeline starts
// allocating per call (closures, enumerators, task boxes), Allocated regresses
// here while the stage-1 sync baseline stays flat.
[ConfiglueModel("bench-214-mixed-transformer", Version = 1)]
public partial class MixedTransformerBenchmarkSettings
{
    public int Counter { get; set; }

    public string Name { get; set; } = string.Empty;
}

[MemoryDiagnoser]
public class MixedTransformerPipelineBenchmarks214
{
    private static readonly IStateByteTransformer SyncNoOp = new NoOpSyncTransformer();
    private static readonly IStateByteTransformer AsyncNoOp = new NoOpAsyncTransformer();

    private SerializedStateWriter<MixedTransformerBenchmarkSettings.Fragment> _writer = null!;
    private SerializedStateReader<MixedTransformerBenchmarkSettings.Fragment> _reader = null!;
    private StateWriteRequest<MixedTransformerBenchmarkSettings.Fragment> _request = default!;
    private ConfiglueResourceContext _context;

    [Params(100, 4096, 65536)]
    public int PayloadSize { get; set; }

    [Params(1, 2, 3)]
    public int StageCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var codec = new JsonStateCodec<MixedTransformerBenchmarkSettings.Fragment>();
        var fragment = new MixedTransformerBenchmarkSettings.Fragment
        {
            Counter = Optional<int>.Present(1),
            Name = Optional<string>.Present(new string('x', PayloadSize)),
        };
        var codecContext = new StateCodecContext(
            new StateSchemaMetadata("bench-214-mixed-transformer", 1)
        );

        // No-op transforms pass bytes through untouched, so the stored bytes
        // are exactly the codec output.
        var buffer = new ArrayBufferWriter<byte>();
        codec.Serialize(fragment, buffer, in codecContext);
        var resource = new CannedMemoryResource(buffer.WrittenMemory.ToArray());

        // Fixed registration order: sync, async, sync. StageCount 1 covers the
        // sync-only dispatch baseline; 2-3 cover mixed sync+async dispatch.
        var stages = new IStateByteTransformer[] { SyncNoOp, AsyncNoOp, SyncNoOp };
        var selected = stages.Take(StageCount).ToArray();
        _writer = new SerializedStateWriter<MixedTransformerBenchmarkSettings.Fragment>(
            resource,
            codec,
            codecContext,
            transformers: selected
        );
        _reader = new SerializedStateReader<MixedTransformerBenchmarkSettings.Fragment>(
            resource,
            codec,
            codecContext,
            transformers: selected
        );
        _context = ConfiglueResourceContext.Default;
        _request = new StateWriteRequest<MixedTransformerBenchmarkSettings.Fragment>(fragment);

        _ = _writer.WriteAsync(_context, _request).AsTask().GetAwaiter().GetResult();
        _ = _reader.ReadAsync(_context).AsTask().GetAwaiter().GetResult();
    }

    [Benchmark]
    public ValueTask<StateWriteResult> PipelineWriteAsync() =>
        _writer.WriteAsync(_context, _request);

    [Benchmark]
    public ValueTask<StateReadResult<MixedTransformerBenchmarkSettings.Fragment>> PipelineReadAsync() =>
        _reader.ReadAsync(_context);

    private sealed class NoOpSyncTransformer : ISynchronousStateByteTransformer
    {
        public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source) => source;

        public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source) => source;
    }

    private sealed class NoOpAsyncTransformer : IAsyncStateByteTransformer
    {
        public ValueTask<ReadOnlyMemory<byte>> TransformReadAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        ) => new(source);

        public ValueTask<ReadOnlyMemory<byte>> TransformWriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        ) => new(source);
    }

    private sealed class CannedMemoryResource(byte[] content)
        : IResourceReader,
            IResourceWriter
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => new(ResourceReadResult.Success(content));

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        ) => new(new StateWriteResult("1"));
    }
}
