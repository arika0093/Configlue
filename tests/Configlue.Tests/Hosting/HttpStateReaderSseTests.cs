using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Configlue.Provider.Json;
using Configlue.Source.Http;

namespace Configlue.Tests;

/// <summary>SSE reconnect/invalidation contract for <see cref="HttpStateReader{TFragment}"/>.</summary>
public sealed class HttpStateReaderSseTests
{
    private static readonly string RevA = new('a', 64);
    private static readonly string RevB = new('b', 64);

    [Test]
    public async Task SseChangedEvent_CompletesWait()
    {
        var initial = Serialize(new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) });
        var changed = Serialize(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
        );
        var stateCalls = 0;
        using var client = new HttpClient(
            new ScriptedHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/events"))
                {
                    return SseResponse("changed", RevB);
                }

                stateCalls++;
                // Initial read + pre-check converge on A; post-SSE convergence reads B.
                return stateCalls <= 2
                    ? JsonResponse(HttpStatusCode.OK, initial, RevA)
                    : JsonResponse(HttpStatusCode.OK, changed, RevB);
            })
        )
        {
            BaseAddress = new Uri("http://localhost"),
        };
        var reader = WatchReader(client);
        await reader.ReadAsync(ConfiglueResourceContext.Default);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await reader.WaitForChangeAsync(ConfiglueResourceContext.Default, RevA, timeout.Token);

        var converged = await reader.ReadAsync(ConfiglueResourceContext.Default);
        converged.Revision.ShouldBe(RevB);
    }

    [Test]
    public async Task SseClosedStream_ConvergesViaReread()
    {
        var initial = Serialize(new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) });
        var changed = Serialize(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) }
        );
        var stateCalls = 0;
        using var client = new HttpClient(
            new ScriptedHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/events"))
                {
                    // Closed stream: the watch loop must converge via re-read + backoff.
                    return SseResponse(null, null);
                }

                stateCalls++;
                // Initial + pre-check see A; the convergence re-read after the
                // transport failure observes B.
                if (stateCalls <= 2)
                {
                    return JsonResponse(HttpStatusCode.OK, initial, RevA);
                }

                return JsonResponse(HttpStatusCode.OK, changed, RevB);
            })
        )
        {
            BaseAddress = new Uri("http://localhost"),
        };
        var reader = WatchReader(client);
        await reader.ReadAsync(ConfiglueResourceContext.Default);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await reader.WaitForChangeAsync(ConfiglueResourceContext.Default, RevA, timeout.Token);
    }

    [Test]
    public async Task WaitAlreadyChanged_ReturnsImmediately()
    {
        var body = Serialize(new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) });
        using var client = new HttpClient(
            new ScriptedHandler(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/events")
                    ? SseResponse("changed", RevB)
                    : JsonResponse(HttpStatusCode.OK, body, RevA)
            )
        )
        {
            BaseAddress = new Uri("http://localhost"),
        };
        var reader = WatchReader(client);
        var read = await reader.ReadAsync(ConfiglueResourceContext.Default);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // Observed revision differs from baseline: no SSE subscription needed.
        await reader.WaitForChangeAsync(
            ConfiglueResourceContext.Default,
            "mismatched-revision",
            timeout.Token
        );
        read.Revision.ShouldBe(RevA);
    }

    private static HttpStateReader<AppSettings.Fragment> WatchReader(HttpClient client) =>
        new(
            client,
            new Uri("http://localhost/api/settings"),
            new Uri("http://localhost/api/settings/events"),
            reconnectInitialDelay: TimeSpan.FromMilliseconds(20),
            reconnectMaxDelay: TimeSpan.FromMilliseconds(100)
        );

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        byte[] body,
        string revision
    )
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(body),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        response.Headers.ETag = EntityTagHeaderValue.Parse($"\"{revision}\"");
        return response;
    }

    private static HttpResponseMessage SseResponse(string? eventType, string? eventId)
    {
        var builder = new StringBuilder();
        if (eventType is not null)
        {
            builder.Append("event: ").Append(eventType).Append('\n');
            builder.Append("id: ").Append(eventId).Append("\n\n");
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(builder.ToString(), Encoding.UTF8, "text/event-stream"),
        };
        return response;
    }

    private static byte[] Serialize(AppSettings.Fragment fragment) =>
        ConfiglueFragmentJson.SerializeToCanonicalBytes(
            fragment,
            typeof(AppSettings.Fragment),
            null
        );

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(responder(request));
    }
}
