using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Configlue;
using Configlue.Resource.Http;

namespace Configlue.Tests;

public sealed class HttpResourceTests
{
    private static readonly Uri EndpointRoot = new("https://settings.example.test/config/");

    [Test]
    public async Task PipelineReader_StreamsContentAndPreservesResponseMetadata()
    {
        var response = ContentResponse(HttpStatusCode.OK, "{\"RetryCount\":5}", "\"revision-1\"");
        response.Headers.TryAddWithoutValidation(
            HttpResourceReader.SchemaIdHeaderName,
            "AppSettings"
        );
        response.Headers.TryAddWithoutValidation(HttpResourceReader.SchemaVersionHeaderName, "3");
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler((_, _) => Task.FromResult(response))
        );
        var reader = new HttpResourceReader(httpClient, EndpointRoot);

        await using var result = await reader.ReadPipelineAsync();
        var content = await result.ReadAllAsync();
        var bytes = content.ToArray();
        result.Content!.AdvanceTo(content.End);

        reader.IsPipelineReadPreferred.ShouldBeTrue();
        result.Status.ShouldBe(StateReadStatus.Success);
        Encoding.UTF8.GetString(bytes).ShouldBe("{\"RetryCount\":5}");
        result.Revision.ShouldBe("\"revision-1\"");
        result.Schema.ShouldBe(new StateSchemaMetadata("AppSettings", 3));
    }

    [Test]
    public async Task Reader_ReturnsContentEtagAndSchemaMetadata()
    {
        var response = ContentResponse(HttpStatusCode.OK, "{\"RetryCount\":5}", "\"revision-1\"");
        response.Headers.TryAddWithoutValidation(
            HttpResourceReader.SchemaIdHeaderName,
            "AppSettings"
        );
        response.Headers.TryAddWithoutValidation(HttpResourceReader.SchemaVersionHeaderName, "3");
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler((_, _) => Task.FromResult(response))
        );
        var reader = new HttpResourceReader(httpClient, EndpointRoot);

        var result = await reader.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        Encoding.UTF8.GetString(result.Content.Span).ShouldBe("{\"RetryCount\":5}");
        result.Revision.ShouldBe("\"revision-1\"");
        result.Schema.ShouldBe(new StateSchemaMetadata("AppSettings", 3));
        reader.GetUri.ShouldBe(new Uri(EndpointRoot, "get"));
    }

    [Test]
    public async Task Reader_MapsMissingAndTransientResponsesButSurfacesClientErrors()
    {
        var missing = await ReadWithStatus(HttpStatusCode.NotFound);
        missing.Status.ShouldBe(StateReadStatus.NotFound);

        var timedOut = await ReadWithStatus(HttpStatusCode.RequestTimeout);
        timedOut.Status.ShouldBe(StateReadStatus.Unavailable);

        var throttled = await ReadWithStatus(HttpStatusCode.TooManyRequests);
        throttled.Status.ShouldBe(StateReadStatus.Unavailable);

        var unavailable = await ReadWithStatus(HttpStatusCode.ServiceUnavailable);
        unavailable.Status.ShouldBe(StateReadStatus.Unavailable);

        var unauthorized = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await ReadWithStatus(HttpStatusCode.Unauthorized);
        });
        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var forbidden = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await ReadWithStatus(HttpStatusCode.Forbidden);
        });
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var invalidRequest = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await ReadWithStatus(HttpStatusCode.BadRequest);
        });
        invalidRequest.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Reader_MapsTransportAndTimeoutFailuresButPreservesCallerCancellation()
    {
        using var failedHttpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                (_, _) => throw new HttpRequestException("network failure")
            )
        );
        var failedReader = new HttpResourceReader(failedHttpClient, EndpointRoot);
        (await failedReader.ReadAsync()).Status.ShouldBe(StateReadStatus.Unavailable);

        using var timedOutHttpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                (_, _) => throw new TaskCanceledException("request timed out")
            )
        );
        var timedOutReader = new HttpResourceReader(timedOutHttpClient, EndpointRoot);
        (await timedOutReader.ReadAsync()).Status.ShouldBe(StateReadStatus.Unavailable);

        using var callerHttpClient = new HttpClient(new BlockingHttpMessageHandler());
        var callerReader = new HttpResourceReader(callerHttpClient, EndpointRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await callerReader.ReadAsync(cancellation.Token);
        });
    }

    [Test]
    public async Task Writer_SendsRevisionAndSchemaHeadersAndReturnsNewEtag()
    {
        CapturedRequest? capturedRequest = null;
        var response = new HttpResponseMessage(HttpStatusCode.NoContent);
        response.Headers.ETag = new EntityTagHeaderValue("\"revision-2\"");
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                async (request, token) =>
                {
                    capturedRequest = await CaptureRequestAsync(request, token);
                    return response;
                }
            )
        );
        var reader = new HttpResourceReader(httpClient, EndpointRoot);
        var writer = reader.CreateWriter();

        var result = await writer.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("{\"RetryCount\":7}"),
                ExpectedRevision: "\"revision-1\"",
                Schema: new StateSchemaMetadata("AppSettings", 3),
                CheckRevision: true
            )
        );

        result.Revision.ShouldBe("\"revision-2\"");
        writer.ResourceId.ShouldBe(reader.ResourceId);
        capturedRequest.ShouldNotBeNull();
        capturedRequest!.Method.ShouldBe(HttpMethod.Put);
        capturedRequest.RequestUri.ShouldBe(new Uri(EndpointRoot, "update"));
        capturedRequest.IfMatch.ShouldBe("\"revision-1\"");
        capturedRequest.IfNoneMatch.ShouldBeNull();
        capturedRequest.SchemaId.ShouldBe("AppSettings");
        capturedRequest.SchemaVersion.ShouldBe("3");
        capturedRequest.ContentType.ShouldBe("application/octet-stream");
        Encoding.UTF8.GetString(capturedRequest.Content).ShouldBe("{\"RetryCount\":7}");
    }

    [Test]
    public async Task Writer_UsesCreateConditionAndMapsPreconditionFailuresToConflicts()
    {
        CapturedRequest? capturedRequest = null;
        using var createHttpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                async (request, token) =>
                {
                    capturedRequest = await CaptureRequestAsync(request, token);
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }
            )
        );
        var createReader = new HttpResourceReader(createHttpClient, EndpointRoot);

        await createReader
            .CreateWriter()
            .WriteAsync(
                new ResourceWriteRequest(
                    Encoding.UTF8.GetBytes("{}"),
                    ExpectedRevision: null,
                    CheckRevision: true
                )
            );
        capturedRequest.ShouldNotBeNull();
        capturedRequest!.IfMatch.ShouldBeNull();
        capturedRequest.IfNoneMatch.ShouldBe("*");

        using var conflictHttpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                (_, _) =>
                    Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed))
            )
        );
        var conflictReader = new HttpResourceReader(conflictHttpClient, EndpointRoot);
        await Should.ThrowAsync<StateConflictException>(async () =>
        {
            await conflictReader
                .CreateWriter()
                .WriteAsync(
                    new ResourceWriteRequest(
                        Encoding.UTF8.GetBytes("{}"),
                        ExpectedRevision: "\"stale\"",
                        CheckRevision: true
                    )
                );
        });

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await conflictReader
                .CreateWriter()
                .WriteAsync(
                    new ResourceWriteRequest(
                        Encoding.UTF8.GetBytes("{}"),
                        ExpectedRevision: "W/\"weak\"",
                        CheckRevision: true
                    )
                );
        });
    }

    [Test]
    public async Task Watcher_PollsWithIfNoneMatchAndReturnsWhenRevisionChanges()
    {
        var responseIndex = 0;
        var requestTags = new List<string?>();
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                (request, _) =>
                {
                    requestTags.Add(request.Headers.IfNoneMatch.SingleOrDefault()?.ToString());
                    responseIndex++;
                    return Task.FromResult(
                        responseIndex switch
                        {
                            1 => ContentResponse(HttpStatusCode.OK, "first", "\"revision-1\""),
                            2 => new HttpResponseMessage(HttpStatusCode.NotModified),
                            _ => ContentResponse(HttpStatusCode.OK, "second", "\"revision-2\""),
                        }
                    );
                }
            )
        );
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { PollingInterval = TimeSpan.FromMilliseconds(2) }
        );
        var initial = await reader.ReadAsync();

        await reader
            .WaitForChangeAsync(initial.Revision)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        requestTags.Count.ShouldBe(3);
        requestTags[0].ShouldBeNull();
        requestTags[1].ShouldBe("\"revision-1\"");
        requestTags[2].ShouldBe("\"revision-1\"");
    }

    [Test]
    public async Task Watcher_DetectsChangedContentWhenServerDoesNotReturnAnEtag()
    {
        var responseIndex = 0;
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                (_, _) =>
                {
                    responseIndex++;
                    return Task.FromResult(
                        ContentResponse(HttpStatusCode.OK, responseIndex == 1 ? "first" : "second")
                    );
                }
            )
        );
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { PollingInterval = TimeSpan.FromMilliseconds(2) }
        );
        var initial = await reader.ReadAsync();

        await reader
            .WaitForChangeAsync(initial.Revision)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        responseIndex.ShouldBe(2);
    }

    [Test]
    public async Task Watcher_HonorsCallerCancellation()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                (_, _) =>
                {
                    requestCount++;
                    return Task.FromResult(
                        requestCount == 1
                            ? ContentResponse(HttpStatusCode.OK, "current", "\"revision-1\"")
                            : new HttpResponseMessage(HttpStatusCode.NotModified)
                    );
                }
            )
        );
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { PollingInterval = TimeSpan.FromMilliseconds(2) }
        );
        var initial = await reader.ReadAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await reader.WaitForChangeAsync(initial.Revision, cancellation.Token);
        });
    }

    [Test]
    public void Constructor_RejectsNonHttpRootsAndPathsThatEscapeTheRoot()
    {
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
            )
        );

        Should.Throw<ArgumentException>(() =>
            new HttpResourceReader(httpClient, new Uri("file:///tmp/config"))
        );
        Should.Throw<ArgumentException>(() =>
            new HttpResourceReader(
                httpClient,
                EndpointRoot,
                new HttpResourceOptions { GetPath = "../outside" }
            )
        );
    }

    private static async Task<ResourceReadResult> ReadWithStatus(HttpStatusCode statusCode)
    {
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                (_, _) => Task.FromResult(new HttpResponseMessage(statusCode))
            )
        );
        var reader = new HttpResourceReader(httpClient, EndpointRoot);
        return await reader.ReadAsync();
    }

    private static HttpResponseMessage ContentResponse(
        HttpStatusCode statusCode,
        string content,
        string? etag = null
    )
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)),
        };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private static async Task<CapturedRequest> CaptureRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) =>
        new(
            request.Method,
            request.RequestUri,
            request.Headers.IfMatch.SingleOrDefault()?.ToString(),
            request.Headers.IfNoneMatch.SingleOrDefault()?.ToString(),
            request.Headers.TryGetValues(HttpResourceReader.SchemaIdHeaderName, out var schemaIds)
                ? schemaIds.Single()
                : null,
            request.Headers.TryGetValues(
                HttpResourceReader.SchemaVersionHeaderName,
                out var versions
            )
                ? versions.Single()
                : null,
            request.Content?.Headers.ContentType?.MediaType,
            request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken)
        );

    private sealed record CapturedRequest
    {
        public HttpMethod Method { get; init; }
        public Uri? RequestUri { get; init; }
        public string? IfMatch { get; init; }
        public string? IfNoneMatch { get; init; }
        public string? SchemaId { get; init; }
        public string? SchemaVersion { get; init; }
        public string? ContentType { get; init; }
        public byte[] Content { get; init; }

        public CapturedRequest(
            HttpMethod Method,
            Uri? RequestUri,
            string? IfMatch,
            string? IfNoneMatch,
            string? SchemaId,
            string? SchemaVersion,
            string? ContentType,
            byte[] Content
        )
        {
            this.Method = Method;
            this.RequestUri = RequestUri;
            this.IfMatch = IfMatch;
            this.IfNoneMatch = IfNoneMatch;
            this.SchemaId = SchemaId;
            this.SchemaVersion = SchemaVersion;
            this.ContentType = ContentType;
            this.Content = Content;
        }

        public void Deconstruct(
            out HttpMethod Method,
            out Uri? RequestUri,
            out string? IfMatch,
            out string? IfNoneMatch,
            out string? SchemaId,
            out string? SchemaVersion,
            out string? ContentType,
            out byte[] Content
        )
        {
            Method = this.Method;
            RequestUri = this.RequestUri;
            IfMatch = this.IfMatch;
            IfNoneMatch = this.IfNoneMatch;
            SchemaId = this.SchemaId;
            SchemaVersion = this.SchemaVersion;
            ContentType = this.ContentType;
            Content = this.Content;
        }
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => send(request, cancellationToken);
    }

    private sealed class BlockingHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The infinite wait completed unexpectedly.");
        }
    }
}
