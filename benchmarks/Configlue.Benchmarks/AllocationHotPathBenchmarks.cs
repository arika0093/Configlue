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

/// <summary>
/// Measures the production transformer orchestration path (<see cref="TransformingResource"/>,
/// which internally runs the ownership-aware <c>StateByteTransformerPipeline</c>) with 0-3 stages
/// over the same payload sizes as <see cref="TransformerAllocationBenchmarks"/>.
/// </summary>
/// <remarks>
/// Registration order follows the production convention (read order): AES is registered before
/// compression, so writes compress-then-encrypt and reads decrypt-then-decompress. The 3-stage
/// pipeline (LZ4 + Zstandard + AES) chains two compression stages before encryption.
/// <para/>
/// Benchmark methods stay synchronous and block on the already-completed <see cref="ValueTask"/>
/// with <c>GetAwaiter().GetResult()</c>, so no benchmark-side async state machine is allocated;
/// every transformer here is synchronous, so the production pipeline also completes synchronously.
/// All setup (payload, transformers, pipelines, write request) happens in <see cref="Setup"/>;
/// each operation consumes only the output length, and the fake resource retains a length
/// (<see cref="int"/>) rather than a buffer, so pooled buffers never leak across iterations.
/// </remarks>
[MemoryDiagnoser]
public class TransformerPipelineAllocationBenchmarks
{
    private byte[] _payload = null!;
    private AesGcmStateByteTransformer _aes = null!;
    private CompressionStateByteTransformer _zstd = null!;
    private CompressionStateByteTransformer _lz4 = null!;
    private ConfiglueResourceContext _context;
    private ResourceWriteRequest _writeRequest;
    private InMemoryResource _writeResource = null!;
    private IResourceWriter _zeroWriter = null!;
    private IResourceWriter _oneStageWriter = null!;
    private IResourceWriter _twoStageWriter = null!;
    private IResourceWriter _threeStageWriter = null!;
    private TransformingResource _zeroReader = null!;
    private TransformingResource _oneStageReader = null!;
    private TransformingResource _twoStageReader = null!;
    private TransformingResource _threeStageReader = null!;

    [Params(100, 4096, 65536)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Setup: build the payload, transformers, production pipelines, and fixtures once.
        // Nothing here runs in the measured region.
        _payload = Enumerable.Range(0, PayloadSize).Select(static value => (byte)value).ToArray();
        _aes = new AesGcmStateByteTransformer(new byte[32]);
        _zstd = new CompressionStateByteTransformer(CompressionAlgorithm.Zstandard);
        _lz4 = new CompressionStateByteTransformer(CompressionAlgorithm.Lz4);
        _context = ConfiglueResourceContext.Default;
        _writeRequest = new ResourceWriteRequest(_payload);
        _writeResource = new InMemoryResource();

        var zeroWrite = new TransformingResource(_writeResource, []);
        var oneStageWrite = new TransformingResource(_writeResource, [_aes]);
        var twoStageWrite = new TransformingResource(_writeResource, [_aes, _zstd]);
        var threeStageWrite = new TransformingResource(_writeResource, [_aes, _zstd, _lz4]);
        _zeroWriter = zeroWrite.Writer!;
        _oneStageWriter = oneStageWrite.Writer!;
        _twoStageWriter = twoStageWrite.Writer!;
        _threeStageWriter = threeStageWrite.Writer!;

        // Read fixtures hold the stored (transformed) form; the measured read path
        // decrypts/decompresses them back through the same production orchestration.
        _zeroReader = new TransformingResource(new InMemoryResource { Stored = _payload }, []);
        _oneStageReader = new TransformingResource(
            new InMemoryResource { Stored = _aes.TransformWrite(_payload) },
            [_aes]
        );
        _twoStageReader = new TransformingResource(
            new InMemoryResource
            {
                Stored = _aes.TransformWrite(_zstd.TransformWrite(_payload)),
            },
            [_aes, _zstd]
        );
        _threeStageReader = new TransformingResource(
            new InMemoryResource
            {
                Stored = _aes.TransformWrite(_zstd.TransformWrite(_lz4.TransformWrite(_payload))),
            },
            [_aes, _zstd, _lz4]
        );

        // Warm up the production path (async-capability cache, JIT) and fail fast when a
        // pipeline does not round-trip.
        ValidateRoundTrip(_zeroWriter, _zeroReader, nameof(_zeroReader));
        ValidateRoundTrip(_oneStageWriter, _oneStageReader, nameof(_oneStageReader));
        ValidateRoundTrip(_twoStageWriter, _twoStageReader, nameof(_twoStageReader));
        ValidateRoundTrip(_threeStageWriter, _threeStageReader, nameof(_threeStageReader));
    }

    [GlobalCleanup]
    public void Cleanup() => _aes.Dispose();

    // Write benchmarks: plaintext payload -> production write pipeline -> fake resource.
    // Each operation consumes the persisted length; the fake keeps only the length.

    [Benchmark(Baseline = true)]
    public int ZeroWrite()
    {
        _zeroWriter.WriteAsync(_context, _writeRequest).GetAwaiter().GetResult();
        return _writeResource.LastWrittenLength;
    }

    [Benchmark]
    public int OneStageAesWrite()
    {
        _oneStageWriter.WriteAsync(_context, _writeRequest).GetAwaiter().GetResult();
        return _writeResource.LastWrittenLength;
    }

    [Benchmark]
    public int TwoStageWrite()
    {
        _twoStageWriter.WriteAsync(_context, _writeRequest).GetAwaiter().GetResult();
        return _writeResource.LastWrittenLength;
    }

    [Benchmark]
    public int ThreeStageWrite()
    {
        _threeStageWriter.WriteAsync(_context, _writeRequest).GetAwaiter().GetResult();
        return _writeResource.LastWrittenLength;
    }

    // Read benchmarks: stored bytes -> production read pipeline -> plaintext (length consumed).

    [Benchmark]
    public int ZeroRead() => _zeroReader.ReadAsync(_context).GetAwaiter().GetResult().Content.Length;

    [Benchmark]
    public int OneStageAesRead() =>
        _oneStageReader.ReadAsync(_context).GetAwaiter().GetResult().Content.Length;

    [Benchmark]
    public int TwoStageRead() =>
        _twoStageReader.ReadAsync(_context).GetAwaiter().GetResult().Content.Length;

    [Benchmark]
    public int ThreeStageRead() =>
        _threeStageReader.ReadAsync(_context).GetAwaiter().GetResult().Content.Length;

    private void ValidateRoundTrip(
        IResourceWriter writer,
        TransformingResource reader,
        string name
    )
    {
        writer.WriteAsync(_context, _writeRequest).GetAwaiter().GetResult();
        var result = reader.ReadAsync(_context).GetAwaiter().GetResult();
        if (!result.Content.Span.SequenceEqual(_payload))
        {
            throw new InvalidOperationException($"The {name} pipeline did not round-trip.");
        }
    }

    /// <summary>
    /// Synchronous in-memory resource. Completed <see cref="ValueTask"/> results allocate nothing,
    /// and writes retain only the content length so pooled buffers never span iterations.
    /// </summary>
    private sealed class InMemoryResource : IResourceReader, IResourceWriter
    {
        public ReadOnlyMemory<byte> Stored;
        public int LastWrittenLength;

        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            _ = cancellationToken;
            return ValueTask.FromResult(ResourceReadResult.Success(Stored));
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            _ = cancellationToken;
            LastWrittenLength = request.Content.Length;
            return ValueTask.FromResult(new StateWriteResult("1"));
        }
    }
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
public class SerializedWriterAllocationBenchmarks
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

        try
        {
            return await plan.BatchWriter.WriteBatchAsync([plan.Mutation]).ConfigureAwait(false);
        }
        finally
        {
            ((object)plan as IDisposable)?.Dispose();
        }
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
