using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Extensibility;
using Configlue.Provider.Json;
using Configlue.Resource.Http;
using Configlue.Resource.S3;
using Configlue.Resources;
using Configlue.State;

[MemoryDiagnoser]
public class HttpStreamingBenchmarks
{
    private byte[] _payload = null!;
    private HttpClient _httpClient = null!;
    private SerializedStateReader<SerializedReadBenchmarkSettings.Fragment> _pipelineReader =
        null!;
    private SerializedStateReader<SerializedReadBenchmarkSettings.Fragment> _bufferedReader =
        null!;

    [Params(262144, 2097152)]
    public int ContentSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Reusable backing data: payload generation allocation stays outside the measured region.
        _payload = Encoding.UTF8.GetBytes(
            $$"""{"$version":1,"Payload":"{{new string('x', ContentSize)}}"}"""
        );
        _httpClient = new HttpClient(new LargePayloadHandler(_payload), disposeHandler: true);
        var resource = new HttpResourceReader(
            _httpClient,
            new Uri("https://settings.example.test/config/")
        );
        _pipelineReader = new SerializedStateReader<SerializedReadBenchmarkSettings.Fragment>(
            resource,
            new JsonStateCodec<SerializedReadBenchmarkSettings.Fragment>
            {
                UseAsyncStreamDecoding = true,
            }
        );
        _bufferedReader = new SerializedStateReader<SerializedReadBenchmarkSettings.Fragment>(
            new MemoryOnlyResourceReader(resource),
            new JsonStateCodec<SerializedReadBenchmarkSettings.Fragment>()
        );
    }

    [GlobalCleanup]
    public void Cleanup() => _httpClient.Dispose();

    [Benchmark(Baseline = true)]
    public ValueTask<
        StateReadResult<SerializedReadBenchmarkSettings.Fragment>
    > BufferedReadAsync() => _bufferedReader.ReadAsync(ConfiglueResourceContext.Default);

    [Benchmark]
    public ValueTask<
        StateReadResult<SerializedReadBenchmarkSettings.Fragment>
    > PipelineReadAsync() => _pipelineReader.ReadAsync(ConfiglueResourceContext.Default);

    private sealed class MemoryOnlyResourceReader(IResourceReader inner) : IResourceReader
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => inner.ReadAsync(context, cancellationToken);
    }

    private sealed class LargePayloadHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Fresh StreamContent per request over the shared backing data: no external network,
            // no per-setup copy. The buffered path copies into a response-sized byte[] while the
            // pipeline path streams through PipeReader without one.
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(payload, writable: false), 81920),
            };
            return Task.FromResult(response);
        }
    }
}

[MemoryDiagnoser]
public class S3StreamingBenchmarks
{
    private byte[] _payload = null!;
    private SerializedStateReader<SerializedReadBenchmarkSettings.Fragment> _pipelineReader =
        null!;
    private SerializedStateReader<SerializedReadBenchmarkSettings.Fragment> _bufferedReader =
        null!;

    [Params(262144, 2097152)]
    public int ContentSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Reusable backing data: payload generation allocation stays outside the measured region.
        _payload = Encoding.UTF8.GetBytes(
            $$"""{"$version":1,"Payload":"{{new string('x', ContentSize)}}"}"""
        );
        var resource = new S3ObjectResource(
            new FakeLargePayloadS3Client(_payload),
            "bench-bucket",
            "bench.json"
        );
        _pipelineReader = new SerializedStateReader<SerializedReadBenchmarkSettings.Fragment>(
            resource,
            new JsonStateCodec<SerializedReadBenchmarkSettings.Fragment>
            {
                UseAsyncStreamDecoding = true,
            }
        );
        _bufferedReader = new SerializedStateReader<SerializedReadBenchmarkSettings.Fragment>(
            new MemoryOnlyResourceReader(resource),
            new JsonStateCodec<SerializedReadBenchmarkSettings.Fragment>()
        );
    }

    [Benchmark(Baseline = true)]
    public ValueTask<
        StateReadResult<SerializedReadBenchmarkSettings.Fragment>
    > BufferedReadAsync() => _bufferedReader.ReadAsync(ConfiglueResourceContext.Default);

    [Benchmark]
    public ValueTask<
        StateReadResult<SerializedReadBenchmarkSettings.Fragment>
    > PipelineReadAsync() => _pipelineReader.ReadAsync(ConfiglueResourceContext.Default);

    private sealed class MemoryOnlyResourceReader(IResourceReader inner) : IResourceReader
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => inner.ReadAsync(context, cancellationToken);
    }

    private sealed class FakeLargePayloadS3Client(byte[] payload)
        : IS3ObjectClient,
            IS3ObjectStreamClient
    {
        public Task<S3ObjectReadResult> GetObjectAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Mirror the production buffered path: copy the response stream into a
            // response-sized byte[] per read.
            var copy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
            return Task.FromResult(new S3ObjectReadResult(copy, "\"bench-1\""));
        }

        public Task<S3ObjectStreamResult> GetObjectStreamAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Streaming path: expose the shared backing data without a response-sized copy.
            // The per-iteration MemoryStream is disposed via the pipeline result owner.
            return Task.FromResult(
                new S3ObjectStreamResult(
                    new MemoryStream(payload, writable: false),
                    "\"bench-1\""
                )
            );
        }

        public Task<S3ObjectWriteResult> PutObjectAsync(
            string bucketName,
            string key,
            ReadOnlyMemory<byte> content,
            string? expectedETag,
            bool requireMissing,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("The streaming benchmark never writes.");
    }
}
