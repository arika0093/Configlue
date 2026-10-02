using System.Net;
using System.Net.Http.Headers;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Resource.Http;

namespace Configlue.Tests;

public sealed class HttpResourceRequestTimeoutTests
{
    private static readonly Uri EndpointRoot = new("https://settings.example.test/config/");
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Test]
    public async Task BufferedRead_RequestTimeoutTerminatesStalledBody()
    {
        var content = new StallingHttpContent();
        using var httpClient = new HttpClient(new StaticContentHandler(content));
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { RequestTimeout = ShortTimeout }
        );

        var result = await reader.ReadAsync().AsTask().WaitAsync(Bound);

        await content.BodyReadStarted.Task.WaitAsync(Bound);
        result.Status.ShouldBe(StateReadStatus.Unavailable);
    }

    [Test]
    public async Task PipelineRead_RequestTimeoutTerminatesStalledBody()
    {
        var content = new StallingHttpContent();
        using var httpClient = new HttpClient(new StaticContentHandler(content));
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { RequestTimeout = ShortTimeout }
        );

        await using var result = await reader.ReadPipelineAsync().AsTask().WaitAsync(Bound);

        result.Status.ShouldBe(StateReadStatus.Success);
        var readTask = result.ReadAllAsync().AsTask();
        await content.BodyReadStarted.Task.WaitAsync(Bound);

        await Should.ThrowAsync<IOException>(async () => await readTask.WaitAsync(Bound));
    }

    [Test]
    public async Task PipelineStateRead_MapsRequestTimeoutToUnavailable()
    {
        var content = new StallingHttpContent();
        using var httpClient = new HttpClient(new StaticContentHandler(content));
        var resource = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { RequestTimeout = ShortTimeout }
        );
        var stateReader = new SerializedStateReader<AppSettings.Fragment>(
            resource,
            new JsonStateCodec<AppSettings.Fragment> { UseAsyncStreamDecoding = true }
        );

        var result = await stateReader.ReadAsync().AsTask().WaitAsync(Bound);

        await content.BodyReadStarted.Task.WaitAsync(Bound);
        result.Status.ShouldBe(StateReadStatus.Unavailable);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task PipelineRead_CallerCancellationRemainsDistinguishable(bool passBodyToken)
    {
        var content = new StallingHttpContent();
        using var httpClient = new HttpClient(new StaticContentHandler(content));
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { RequestTimeout = TimeSpan.FromSeconds(30) }
        );

        using var caller = new CancellationTokenSource();
        await using var result = await reader
            .ReadPipelineAsync(ConfiglueResourceContext.Default, caller.Token)
            .AsTask()
            .WaitAsync(Bound);
        result.Status.ShouldBe(StateReadStatus.Success);

        var readTask = result
            .ReadAllAsync(passBodyToken ? caller.Token : CancellationToken.None)
            .AsTask();
        await content.BodyReadStarted.Task.WaitAsync(Bound);
        caller.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await readTask.WaitAsync(Bound)
        );
    }

    [Test]
    public async Task PipelineRead_SeparateBodyCancellationRemainsDistinguishable()
    {
        var content = new StallingHttpContent();
        using var httpClient = new HttpClient(new StaticContentHandler(content));
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { RequestTimeout = TimeSpan.FromSeconds(30) }
        );
        await using var result = await reader.ReadPipelineAsync().AsTask().WaitAsync(Bound);
        using var bodyCancellation = new CancellationTokenSource();
        var read = result.ReadAllAsync(bodyCancellation.Token).AsTask();
        await content.BodyReadStarted.Task.WaitAsync(Bound);
        bodyCancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await read.WaitAsync(Bound)
        );
    }

    [Test]
    public async Task BufferedRead_CallerCancellationRemainsDistinguishable()
    {
        var content = new StallingHttpContent();
        using var httpClient = new HttpClient(new StaticContentHandler(content));
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { RequestTimeout = TimeSpan.FromSeconds(30) }
        );

        using var caller = new CancellationTokenSource();
        var readTask = reader.ReadAsync(ConfiglueResourceContext.Default, caller.Token).AsTask();
        await content.BodyReadStarted.Task.WaitAsync(Bound);
        caller.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await readTask.WaitAsync(Bound)
        );
    }

    private sealed class StaticContentHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.ETag = new EntityTagHeaderValue("\"revision-1\"");
            return Task.FromResult(response);
        }
    }

    private sealed class StallingHttpContent : HttpContent
    {
        public TaskCompletionSource BodyReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            StallAsync(CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken
        ) => StallAsync(cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new StallingStream(BodyReadStarted));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private async Task StallAsync(CancellationToken cancellationToken)
        {
            BodyReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The stalled body completed unexpectedly.");
        }
    }

    private sealed class StallingStream(TaskCompletionSource bodyReadStarted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            bodyReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The stalled read completed unexpectedly.");
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
