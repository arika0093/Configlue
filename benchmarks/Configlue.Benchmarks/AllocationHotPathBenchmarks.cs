using System.Buffers;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Codecs;
using Configlue.Extensibility;
using Configlue.Provider.Json;
using Configlue.Provider.MessagePack;
using Configlue.Resource.Redis;
using Configlue.Resources;
using Configlue.State;
using Configlue.Transformer.AES;
using Configlue.Transformer.Compression;
using Configlue.Transformers;

[ConfiglueModel("bench-allocation-messagepack", Version = 1)]
public partial class AllocationMessagePackSettings
{
    public int Counter { get; set; }

    public string Name { get; set; } = string.Empty;
}

[MemoryDiagnoser]
public sealed class TransformerAllocationBenchmarks
{
    private AesGcmStateByteTransformer _aes = null!;
    private CompressionStateByteTransformer _compression = null!;
    private byte[] _payload = null!;

    [Params(100, 4096, 65536)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = Enumerable.Range(0, PayloadSize).Select(static value => (byte)value).ToArray();
        _aes = new AesGcmStateByteTransformer(new byte[32]);
        _compression = new CompressionStateByteTransformer(CompressionAlgorithm.Zstandard);
    }

    [GlobalCleanup]
    public void Cleanup() => _aes.Dispose();

    [Benchmark(Baseline = true)]
    public ValueTask<ReadOnlyMemory<byte>> None() => new(_payload);

    [Benchmark]
    public ReadOnlyMemory<byte> Aes() => _aes.TransformWrite(_payload);

    [Benchmark]
    public ReadOnlyMemory<byte> Compression() => _compression.TransformWrite(_payload);

    [Benchmark]
    public ReadOnlyMemory<byte> CompressionThenAes() =>
        _aes.TransformWrite(_compression.TransformWrite(_payload));
}

[MemoryDiagnoser]
public sealed class PipelineFingerprintAllocationBenchmarks
{
    private byte[] _payload = null!;
    private Action<string> _fingerprintSink = null!;

    [Params(100, 4096, 65536)]
    public int PayloadSize { get; set; }

    [Params(1, 128)]
    public int ChunkSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = Enumerable.Range(0, PayloadSize).Select(static value => (byte)value).ToArray();
        _fingerprintSink = static _ => { };
    }

    [Benchmark]
    public async Task ReadAndFingerprintAsync()
    {
        await using var result = PipelineResourceReader.FromStream(
            new ChunkedMemoryStream(_payload, ChunkSize),
            contentFingerprintCompleted: _fingerprintSink
        );
        _ = await result.ReadAllAsync().ConfigureAwait(false);
    }

    private sealed class ChunkedMemoryStream(byte[] content, int chunkSize) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}

[MemoryDiagnoser]
public sealed class MessagePackCodecAllocationBenchmarks
{
    private MessagePackStateCodec<AllocationMessagePackSettings.Fragment> _codec = null!;
    private AllocationMessagePackSettings.Fragment _value = null!;
    private StateCodecContext _context;
    private ReadOnlySequence<byte> _serialized;
    private ArrayBufferWriter<byte> _destination = null!;

    [Params(false, true)]
    public bool IncludeSchema { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _codec = new MessagePackStateCodec<AllocationMessagePackSettings.Fragment>();
        _value = new AllocationMessagePackSettings.Fragment
        {
            Counter = Optional<int>.Present(42),
            Name = Optional<string>.Present("allocation benchmark"),
        };
        _context = IncludeSchema
            ? new StateCodecContext(new StateSchemaMetadata("bench-allocation-messagepack", 1))
            : default;
        _destination = new ArrayBufferWriter<byte>();
        _codec.Serialize(_value, _destination, in _context);
        _serialized = new ReadOnlySequence<byte>(_destination.WrittenMemory.ToArray());
        _ = _codec.Deserialize(in _serialized, in _context);
        _ = _codec.ReadSchemaMetadata(in _serialized);
    }

    [Benchmark]
    public int Serialize()
    {
        _destination.Clear();
        _codec.Serialize(_value, _destination, in _context);
        return _destination.WrittenCount;
    }

    [Benchmark]
    public AllocationMessagePackSettings.Fragment? Deserialize() =>
        _codec.Deserialize(in _serialized, in _context);

    [Benchmark]
    public StateSchemaMetadata? ReadMetadataAndDecodeValue()
    {
        var schema = _codec.ReadSchemaMetadata(in _serialized);
        _ = _codec.Deserialize(in _serialized, in _context);
        return schema;
    }
}

[MemoryDiagnoser]
public sealed class RedisIdentityAllocationBenchmarks
{
    private RedisResource _resource = null!;

    [GlobalSetup]
    public void Setup() => _resource = new RedisResource(_ => null!, "benchmark-allocation");

    [GlobalCleanup]
    public void Cleanup() => _resource.Dispose();

    [Benchmark]
    public ResourceId CreateResourceIdentity() =>
        _resource.GetResourceId(ConfiglueResourceContext.Default);
}

[MemoryDiagnoser]
public sealed class SerializedWriterAllocationBenchmarks
{
    private SerializedStateWriter<OptimizationBenchmarkSettings.Fragment> _writer = null!;
    private ConfiglueResourceContext _context;
    private StateWriteRequest<OptimizationBenchmarkSettings.Fragment> _request = default!;

    [Params(100, 4096, 65536)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _writer = new SerializedStateWriter<OptimizationBenchmarkSettings.Fragment>(
            new NoOpResourceWriter(),
            new JsonStateCodec<OptimizationBenchmarkSettings.Fragment>()
        );
        _context = ConfiglueResourceContext.Default;
        _request = new StateWriteRequest<OptimizationBenchmarkSettings.Fragment>(
            new OptimizationBenchmarkSettings.Fragment
            {
                Counter = Optional<int>.Present(1),
                Name = Optional<string>.Present(new string('x', PayloadSize)),
                Enabled = Optional<bool>.Present(true),
            }
        );
    }

    [Benchmark]
    public ValueTask<StateWriteResult> NormalWriteAsync() =>
        _writer.WriteAsync(_context, _request);

    [Benchmark]
    public async ValueTask<StateWriteResult> BatchWriteAsync()
    {
        var plan = await _writer.TryCreateBatchWriteAsync(_context, _request).ConfigureAwait(false);
        if (plan is null)
        {
            throw new InvalidOperationException("The no-op writer did not prepare a batch plan.");
        }

        return await plan.BatchWriter.WriteBatchAsync([plan.Mutation]).ConfigureAwait(false);
    }

    private sealed class NoOpResourceWriter : IResourceBatchWriter
    {
        private static readonly ResourceId Identity = new("benchmark:no-op");

        public ResourceId GetResourceId(ConfiglueResourceContext context)
        {
            _ = context;
            return Identity;
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(new StateWriteResult("1"));

        public ValueTask<StateWriteResult> WriteBatchAsync(
            IReadOnlyList<ResourceWriteMutation> mutations,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(new StateWriteResult("1"));
    }
}
