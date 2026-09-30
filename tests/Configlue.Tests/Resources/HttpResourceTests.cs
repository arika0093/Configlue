using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Configlue;
using Configlue.Provider.Json;
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
    public async Task SubjectAwareEndpointSelectionRoutesReadsWritesAndResourceIdentity()
    {
        var requests = new List<(HttpMethod Method, Uri Uri)>();
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                (request, _) =>
                {
                    requests.Add((request.Method, request.RequestUri!));
                    var response = ContentResponse(HttpStatusCode.OK, "{}", "\"revision-1\"");
                    if (request.Method == HttpMethod.Put)
                    {
                        response.StatusCode = HttpStatusCode.NoContent;
                    }

                    return Task.FromResult(response);
                }
            )
        );
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions
            {
                EndpointRootSelector = context => new Uri(
                    $"https://settings.example.test/{context.Route.Value}/{context.Key.Value}/"
                ),
            }
        );
        var firstSubject = new ResourceSubject("one");
        var secondSubject = new ResourceSubject("two");
        var firstContext = new ConfiglueResourceContext(
            firstSubject,
            firstSubject.Key,
            RouteKey.From("jp")
        );
        var secondContext = new ConfiglueResourceContext(
            secondSubject,
            secondSubject.Key,
            RouteKey.From("eu")
        );

        await reader.ReadAsync(firstContext);
        await reader.ReadAsync(secondContext);
        await reader
            .CreateWriter()
            .WriteAsync(firstContext, new ResourceWriteRequest(Encoding.UTF8.GetBytes("{}")));

        requests
            .Select(static request => request.Uri.AbsolutePath)
            .ShouldBe([
                $"/jp/{firstSubject.Key.Value}/get",
                $"/eu/{secondSubject.Key.Value}/get",
                $"/jp/{firstSubject.Key.Value}/update",
            ]);
        reader.GetResourceId(firstContext).ShouldNotBe(reader.GetResourceId(secondContext));
    }

    [Test]
    public async Task SubjectAwareWatcherPollsTheSelectedEndpoint()
    {
        var currentETag = "\"revision-1\"";
        var firstConditionalPoll = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var conditionalUris = new ConcurrentQueue<Uri>();
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                (request, _) =>
                {
                    if (request.Headers.IfNoneMatch.Count > 0)
                    {
                        conditionalUris.Enqueue(request.RequestUri!);
                        firstConditionalPoll.TrySetResult();
                        var observed = request.Headers.IfNoneMatch.Single().Tag;
                        if (observed == Volatile.Read(ref currentETag))
                        {
                            return Task.FromResult(
                                new HttpResponseMessage(HttpStatusCode.NotModified)
                            );
                        }
                    }

                    return Task.FromResult(
                        ContentResponse(HttpStatusCode.OK, "{}", Volatile.Read(ref currentETag))
                    );
                }
            )
        );
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions
            {
                PollingInterval = TimeSpan.FromMilliseconds(5),
                EndpointRootSelector = context => new Uri(
                    $"https://settings.example.test/{context.Route.Value}/{context.Key.Value}/"
                ),
            }
        );
        var subject = new ResourceSubject("watcher");
        var context = new ConfiglueResourceContext(subject, subject.Key, RouteKey.From("jp"));

        var waiting = reader.WaitForChangeAsync(context, "\"revision-1\"").AsTask();
        await firstConditionalPoll.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Volatile.Write(ref currentETag, "\"revision-2\"");
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));

        conditionalUris.ShouldNotBeEmpty();
        conditionalUris.ShouldAllBe(uri => uri.AbsolutePath == $"/jp/{subject.Key.Value}/get");
    }

    [Test]
    public async Task Reader_MapsMissingAndTransientResponsesButSurfacesClientErrors()
    {
        var missing = await ReadWithStatus(HttpStatusCode.NotFound);
        missing.Status.ShouldBe(StateReadStatus.NotFound);

        var timedOut = await ReadWithStatus(HttpStatusCode.RequestTimeout);
        timedOut.Status.ShouldBe(StateReadStatus.Unavailable);

        var throttled = await ReadWithStatus((HttpStatusCode)429);
        throttled.Status.ShouldBe(StateReadStatus.Unavailable);

        var unavailable = await ReadWithStatus(HttpStatusCode.ServiceUnavailable);
        unavailable.Status.ShouldBe(StateReadStatus.Unavailable);

        var unauthorized = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await ReadWithStatus(HttpStatusCode.Unauthorized);
        });
#if !NETFRAMEWORK
        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
#endif

        var forbidden = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await ReadWithStatus(HttpStatusCode.Forbidden);
        });
#if !NETFRAMEWORK
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
#endif

        var invalidRequest = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await ReadWithStatus(HttpStatusCode.BadRequest);
        });
#if !NETFRAMEWORK
        invalidRequest.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
#endif
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

        using var requestTimeoutClient = new HttpClient(new BlockingHttpMessageHandler());
        var requestTimeoutReader = new HttpResourceReader(
            requestTimeoutClient,
            EndpointRoot,
            new HttpResourceOptions { RequestTimeout = TimeSpan.FromMilliseconds(25) }
        );
        (await requestTimeoutReader.ReadAsync()).Status.ShouldBe(StateReadStatus.Unavailable);

        using var callerHttpClient = new HttpClient(new BlockingHttpMessageHandler());
        var callerReader = new HttpResourceReader(callerHttpClient, EndpointRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await callerReader.ReadAsync(cancellation.Token);
        });
    }

    [Test]
    public void Reader_RejectsInvalidRequestTimeoutAndPollingBackoff()
    {
        using var httpClient = new HttpClient(new BlockingHttpMessageHandler());

        Should.Throw<ArgumentOutOfRangeException>(() =>
            new HttpResourceReader(
                httpClient,
                EndpointRoot,
                new HttpResourceOptions { RequestTimeout = TimeSpan.Zero }
            )
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new HttpResourceReader(
                httpClient,
                EndpointRoot,
                new HttpResourceOptions
                {
                    PollingInterval = TimeSpan.FromSeconds(2),
                    MaximumPollingInterval = TimeSpan.FromSeconds(1),
                }
            )
        );
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
                Condition: RevisionCondition.FromRevision("\"revision-1\""),
                Schema: new StateSchemaMetadata("AppSettings", 3)
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
                    Condition: RevisionCondition.MustNotExist
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
                        Condition: RevisionCondition.FromRevision("\"stale\"")
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
                        Condition: RevisionCondition.FromRevision("W/\"weak\"")
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
    public async Task AsyncPipelineDecode_FinalizesHttpContentFingerprintForWatching()
    {
        var responseIndex = 0;
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                (_, _) =>
                {
                    responseIndex++;
                    return Task.FromResult(
                        ContentResponse(HttpStatusCode.OK, $$"""{"RetryCount":{{responseIndex}}}""")
                    );
                }
            )
        );
        var resource = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { PollingInterval = TimeSpan.FromMilliseconds(2) }
        );
        var stateReader = new SerializedStateReader<AppSettings.Fragment>(
            resource,
            new JsonStateCodec<AppSettings.Fragment> { UseAsyncStreamDecoding = true }
        );

        var initial = await stateReader.ReadAsync();
        await resource
            .WaitForChangeAsync(initial.Revision)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        initial.Value!.RetryCount.Value.ShouldBe(1);
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
    public async Task Watcher_MultiplexesHighFanOutOnOnePhysicalResource()
    {
        var requestCount = 0;
        var firstWaiterCancelled = 0;
        var pollAfterCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var activeRequests = 0;
        var maximumConcurrentRequests = 0;
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                async (_, cancellationToken) =>
                {
                    var active = Interlocked.Increment(ref activeRequests);
                    while (true)
                    {
                        var observed = Volatile.Read(ref maximumConcurrentRequests);
                        if (observed >= active)
                        {
                            break;
                        }

                        if (
                            Interlocked.CompareExchange(
                                ref maximumConcurrentRequests,
                                active,
                                observed
                            ) == observed
                        )
                        {
                            break;
                        }
                    }

                    try
                    {
                        var requestIndex = Interlocked.Increment(ref requestCount);
                        if (Volatile.Read(ref firstWaiterCancelled) != 0)
                        {
                            pollAfterCancellation.TrySetResult();
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(2), cancellationToken);
                        return requestIndex == 1
                            ? ContentResponse(HttpStatusCode.OK, "current", "\"revision-1\"")
                            : new HttpResponseMessage(HttpStatusCode.NotModified);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeRequests);
                    }
                }
            )
        );
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { PollingInterval = TimeSpan.FromMilliseconds(4) }
        );
        var initial = await reader.ReadAsync();
        using var firstCancellation = new CancellationTokenSource();
        using var remainingCancellation = new CancellationTokenSource();
        var waits = Enumerable
            .Range(0, 32)
            .Select(index =>
                reader
                    .WaitForChangeAsync(
                        initial.Revision,
                        index == 0 ? firstCancellation.Token : remainingCancellation.Token
                    )
                    .AsTask()
            )
            .ToArray();

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        maximumConcurrentRequests.ShouldBe(1);
        requestCount.ShouldBeLessThan(20);

        firstCancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await waits[0]);
        Volatile.Write(ref firstWaiterCancelled, 1);
        await pollAfterCancellation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        waits.Skip(1).ShouldAllBe(static wait => !wait.IsCompleted);

        remainingCancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Task.WhenAll(waits.Skip(1))
        );
    }

    [Test]
    public async Task Watcher_HandlesConcurrentSubscribeDisposeAndChange()
    {
        var requestCount = 0;
        var pollStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseChange = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var httpClient = new HttpClient(
            new DelegateHttpMessageHandler(
                async (_, cancellationToken) =>
                {
                    var requestIndex = Interlocked.Increment(ref requestCount);
                    if (requestIndex == 1)
                    {
                        return ContentResponse(HttpStatusCode.OK, "current", "\"revision-1\"");
                    }

                    if (requestIndex == 2)
                    {
                        pollStarted.TrySetResult();
                        await releaseChange.Task.WaitAsync(cancellationToken);
                        return ContentResponse(HttpStatusCode.OK, "changed", "\"revision-2\"");
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }
            )
        );
        var reader = new HttpResourceReader(
            httpClient,
            EndpointRoot,
            new HttpResourceOptions { PollingInterval = TimeSpan.FromMilliseconds(2) }
        );
        var initial = await reader.ReadAsync();
        var activeWaiters = Enumerable
            .Range(0, 12)
            .Select(_ => reader.WaitForChangeAsync(initial.Revision).AsTask())
            .ToArray();
        await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var index = 0; index < 40; index++)
        {
            using var cancellation = new CancellationTokenSource();
            var transientWaiter = reader
                .WaitForChangeAsync(initial.Revision, cancellation.Token)
                .AsTask();
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () => await transientWaiter);
        }

        releaseChange.TrySetResult();
        await Task.WhenAll(activeWaiters).WaitAsync(TimeSpan.FromSeconds(2));
        requestCount.ShouldBe(2);
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

    private sealed record ResourceSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }
}
