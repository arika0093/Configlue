using System.Net;
using System.Net.Http.Headers;
using Configlue.Provider.Json;
using Configlue.Source.Http;
using SparseFragments.JsonPatch;

namespace Configlue.Tests;

/// <summary>PUT/PATCH protocol contract for <see cref="HttpStateReader{TFragment}"/>.</summary>
public sealed class HttpStateReaderPutPatchTests
{
    private static readonly string RevA = new('a', 64);
    private static readonly string RevB = new('b', 64);

    [Test]
    public async Task PutSuccess_ReturnsNewRevision()
    {
        var updated = Serialize(new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) });
        string? ifMatch = null;
        using var client = StubClient(request =>
        {
            ifMatch = request.Headers.IfMatch.ToString();
            return JsonResponse(HttpStatusCode.OK, updated, RevB);
        });
        var reader = WritableReader(client);

        var result = await reader.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) },
                RevisionCondition.Match(RevA)
            )
        );
        result.Revision.ShouldBe(RevB);
        // No baseline cached, so the writer uses PUT (with the caller's If-Match).
        ifMatch.ShouldNotBeNull();
        ifMatch.ShouldContain(RevA);
    }

    [Test]
    public async Task PutStale_MapsToStaleException()
    {
        using var client = StubClient(_ => ErrorResponse(
            HttpStatusCode.PreconditionFailed,
            "stale"
        ));
        var reader = WritableReader(client);

        await Should.ThrowAsync<HttpStateStaleException>(async () =>
            await reader.WriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
                )
            )
        );
    }

    [Test]
    public async Task PutValidationAndConflict_Mapping()
    {
        using var validationClient = StubClient(_ => ErrorResponse((HttpStatusCode)422, "bad"));
        await Should.ThrowAsync<HttpStateValidationException>(async () =>
            await WritableReader(validationClient).WriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
                )
            )
        );

        using var conflictClient = StubClient(_ => ErrorResponse(
            HttpStatusCode.Conflict,
            "conflict"
        ));
        await Should.ThrowAsync<HttpStateWriteConflictException>(async () =>
            await WritableReader(conflictClient).WriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
                )
            )
        );
    }

    [Test]
    public async Task PatchAsync_RoundtripAndStale()
    {
        var body = Serialize(new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) });
        using var client = StubClient(request =>
            request.Headers.IfMatch.ToString().Contains(RevA)
                ? JsonResponse(HttpStatusCode.OK, body, RevB, "application/json")
                : ErrorResponse(HttpStatusCode.PreconditionFailed, "stale")
        );
        var reader = WritableReader(client);
        var document = SparseJsonPatch.Parse(
            """[{"op":"replace","path":"/RetryCount","value":9}]"""
        );

        var result = await reader.PatchAsync(document, RevA);
        result.Value!.RetryCount.Value.ShouldBe(9);
        result.Revision.ShouldBe(RevB);

        await Should.ThrowAsync<HttpStateStaleException>(async () =>
            await reader.PatchAsync(document, new string('c', 64))
        );
    }

    [Test]
    public async Task WriterUsesPatchWhenBaselineCached_OtherwisePut()
    {
        var calls = new List<string>();
        var current = Serialize(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        using var client = new HttpClient(
            new RecordingHandler(
                calls,
                request =>
                {
                    if (request.Method == HttpMethod.Get)
                    {
                        return JsonResponse(HttpStatusCode.OK, current, RevA);
                    }

                    if (request.Method.Method == "PATCH")
                    {
                        current = Serialize(
                            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
                        );
                        return JsonResponse(HttpStatusCode.OK, current, RevB);
                    }

                    return JsonResponse(HttpStatusCode.OK, current, RevB);
                }
            )
        )
        {
            BaseAddress = new Uri("http://localhost"),
        };
        var reader = WritableReader(client);

        // No baseline: PUT without a preliminary GET-derived PATCH.
        await reader.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) },
                RevisionCondition.Match(RevA)
            )
        );
        calls.ShouldContain("PUT");
        calls.ShouldNotContain("PATCH");

        // After a read the baseline is cached: the writer derives and sends a PATCH.
        calls.Clear();
        var baseline = await reader.ReadAsync(ConfiglueResourceContext.Default);
        await reader.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) },
                RevisionCondition.Match(baseline.Revision!)
            )
        );
        calls.ShouldContain("PATCH");
    }

    [Test]
    public async Task WriterFallsBackToPutWhenPatchUnsupported()
    {
        var calls = new List<string>();
        var current = Serialize(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        using var client = new HttpClient(
            new RecordingHandler(
                calls,
                request =>
                {
                    if (request.Method == HttpMethod.Get)
                    {
                        return JsonResponse(HttpStatusCode.OK, current, RevA);
                    }

                    if (request.Method.Method == "PATCH")
                    {
                        return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                    }

                    current = Serialize(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
                    );
                    return JsonResponse(HttpStatusCode.OK, current, RevB);
                }
            )
        )
        {
            BaseAddress = new Uri("http://localhost"),
        };
        var reader = WritableReader(client);
        var baseline = await reader.ReadAsync(ConfiglueResourceContext.Default);
        await reader.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) },
                RevisionCondition.Match(baseline.Revision!)
            )
        );

        calls.ShouldContain("PATCH");
        calls.ShouldContain("PUT");
    }

    private static HttpStateReader<AppSettings.Fragment> WritableReader(HttpClient client) =>
        new(
            client,
            new Uri("http://localhost/api/settings"),
            new Uri("http://localhost/api/settings/events"),
            writable: true,
            watchEnabled: false
        );

    private static HttpClient StubClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder
    ) =>
        new(new StubHandler(responder)) { BaseAddress = new Uri("http://localhost") };

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        byte[] body,
        string revision,
        string mediaType = "application/json"
    )
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(body),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        response.Headers.ETag = EntityTagHeaderValue.Parse($"\"{revision}\"");
        return response;
    }

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status, string detail)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent($"{{\"detail\":\"{detail}\"}}"),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/problem+json");
        return response;
    }

    private static byte[] Serialize(AppSettings.Fragment fragment) =>
        ConfiglueFragmentJson.SerializeToCanonicalBytes(
            fragment,
            typeof(AppSettings.Fragment),
            null
        );

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(responder(request));
    }

    private sealed class RecordingHandler(
        List<string> calls,
        Func<HttpRequestMessage, HttpResponseMessage> responder
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            lock (calls)
            {
                calls.Add(request.Method.Method);
            }

            return Task.FromResult(responder(request));
        }
    }
}
