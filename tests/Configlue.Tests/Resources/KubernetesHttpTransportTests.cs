using System.Net;
using System.Text;
using System.Text.Json;
using Configlue.Resource.Kubernetes;

namespace Configlue.Tests;

/// <summary>
/// Focused HTTP transport contracts for <see cref="KubernetesConfiguration"/>.
/// Exercises the real internal transport with a scripted handler: no cluster required.
/// </summary>
public sealed class KubernetesHttpTransportTests
{
    [Test]
    public async Task GetConfigMap_BuildsPathAndDecodesDataAndBinary()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(
            """{"metadata":{"resourceVersion":"100"},"data":{"appsettings.json":"{\"RetryCount\":5}"},"binaryData":{"logo":"AQID"}}"""
        );
        using var httpClient = CreateHttpClient(handler);
        var client = KubernetesConfiguration.CreateObjectClient(httpClient);

        var snapshot = await client.GetConfigMapAsync("app-config", "settings", CancellationToken.None);

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Get);
        handler.Requests[0].PathAndQuery.ShouldBe("/api/v1/namespaces/app-config/configmaps/settings");
        snapshot.ResourceVersion.ShouldBe("100");
        snapshot.Data["appsettings.json"].ShouldBe("""{"RetryCount":5}""");
        snapshot.BinaryData["logo"].ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task GetSecret_BuildsPathAndDecodesBase64()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(
            """{"metadata":{"resourceVersion":"7"},"data":{"password":"czNjcjN0"}}"""
        );
        using var httpClient = CreateHttpClient(handler);
        var client = KubernetesConfiguration.CreateObjectClient(httpClient);

        var snapshot = await client.GetSecretAsync("app-config", "credentials", CancellationToken.None);

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Get);
        handler.Requests[0].PathAndQuery.ShouldBe("/api/v1/namespaces/app-config/secrets/credentials");
        snapshot.ResourceVersion.ShouldBe("7");
        snapshot.Data["password"].ShouldBe(Encoding.UTF8.GetBytes("s3cr3t"));
    }

    [Test]
    public async Task ReplaceConfigMap_PutEmitsPayloadAndMapsConflict()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(
            """{"metadata":{"resourceVersion":"2"},"data":{"key":"new"},"binaryData":{"blob":"CQ=="}}"""
        );
        handler.EnqueueStatus(HttpStatusCode.Conflict);
        using var httpClient = CreateHttpClient(handler);
        var client = KubernetesConfiguration.CreateObjectClient(httpClient);

        var revision = await client.ReplaceConfigMapAsync(
            "app-config",
            "settings",
            new Dictionary<string, string> { ["key"] = "new" },
            new Dictionary<string, byte[]> { ["blob"] = [9] },
            "1",
            requireMissing: false,
            CancellationToken.None
        );

        revision.ShouldBe("2");
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Put);
        handler.Requests[0].PathAndQuery.ShouldBe("/api/v1/namespaces/app-config/configmaps/settings");
        using (var payload = JsonDocument.Parse(handler.Requests[0].Body!))
        {
            payload.RootElement.GetProperty("data").GetProperty("key").GetString().ShouldBe("new");
            payload.RootElement.GetProperty("binaryData").GetProperty("blob").GetString().ShouldBe("CQ==");
            payload
                .RootElement.GetProperty("metadata")
                .GetProperty("resourceVersion")
                .GetString()
                .ShouldBe("1");
        }

        await Should.ThrowAsync<KubernetesConflictException>(async () =>
            await client.ReplaceConfigMapAsync(
                "app-config",
                "settings",
                new Dictionary<string, string> { ["key"] = "stale" },
                new Dictionary<string, byte[]>(),
                "1",
                requireMissing: false,
                CancellationToken.None
            )
        );
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].Method.ShouldBe(HttpMethod.Put);
    }

    [Test]
    public async Task CreateSecret_PostToCollectionAndMapsConflict()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueStatus(HttpStatusCode.Conflict);
        using var httpClient = CreateHttpClient(handler);
        var client = KubernetesConfiguration.CreateObjectClient(httpClient);

        await Should.ThrowAsync<KubernetesConflictException>(async () =>
            await client.ReplaceSecretAsync(
                "app-config",
                "credentials",
                new Dictionary<string, byte[]> { ["password"] = Encoding.UTF8.GetBytes("s3cr3t") },
                expectedResourceVersion: null,
                requireMissing: true,
                CancellationToken.None
            )
        );

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Post);
        handler.Requests[0].PathAndQuery.ShouldBe("/api/v1/namespaces/app-config/secrets");
        using (var payload = JsonDocument.Parse(handler.Requests[0].Body!))
        {
            payload
                .RootElement.GetProperty("data")
                .GetProperty("password")
                .GetString()
                .ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("s3cr3t")));
        }
    }

    [Test]
    public async Task WatchSecret_BuildsQueryAndDecodesBookmarkAndModified()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson(
            "{\"type\":\"BOOKMARK\",\"object\":{\"metadata\":{\"resourceVersion\":\"7\"}}}\n"
                + "{\"type\":\"MODIFIED\",\"object\":{\"metadata\":{\"resourceVersion\":\"8\"},\"data\":{\"password\":\"czNjcjN0\"}}}\n"
        );
        using var httpClient = CreateHttpClient(handler);
        var client = KubernetesConfiguration.CreateObjectClient(httpClient);

        var events = new List<KubernetesWatchEvent>();
        await foreach (
            var watchEvent in client.WatchSecretAsync(
                "app-config",
                "credentials",
                "6",
                CancellationToken.None
            )
        )
        {
            events.Add(watchEvent);
        }

        events.Count.ShouldBe(2);
        events[0].Type.ShouldBe(KubernetesWatchType.Bookmark);
        events[0].ResourceVersion.ShouldBe("7");
        events[0].Secret.ShouldBeNull();
        events[1].Type.ShouldBe(KubernetesWatchType.Modified);
        events[1].ResourceVersion.ShouldBe("8");
        events[1].Secret.ShouldNotBeNull();
        events[1].Secret!.Data["password"].ShouldBe(Encoding.UTF8.GetBytes("s3cr3t"));

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Get);
        var query = handler.Requests[0].PathAndQuery;
        query.ShouldContain("/api/v1/namespaces/app-config/secrets");
        query.ShouldContain("watch=true");
        query.ShouldContain("fieldSelector=metadata.name%3Dcredentials");
        query.ShouldContain("resourceVersion=6");
    }

    [Test]
    public async Task WatchConfigMap_GoneMapsToExpired()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueStatus(HttpStatusCode.Gone);
        using var httpClient = CreateHttpClient(handler);
        var client = KubernetesConfiguration.CreateObjectClient(httpClient);

        await Should.ThrowAsync<KubernetesResourceExpiredException>(async () =>
        {
            await foreach (
                var _ in client.WatchConfigMapAsync(
                    "app-config",
                    "settings",
                    "99",
                    CancellationToken.None
                )
            )
            {
            }
        });

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].PathAndQuery.ShouldContain("watch=true");
        handler.Requests[0].PathAndQuery.ShouldContain("resourceVersion=99");
    }

    private static HttpClient CreateHttpClient(ScriptedHandler handler) =>
        new(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("https://k8s.example:6443"),
        };

    private sealed record CapturedRequest(HttpMethod Method, string PathAndQuery, string? Body);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<CapturedRequest> Requests { get; } = [];

        public void EnqueueJson(string json) =>
            _responses.Enqueue(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                }
            );

        public void EnqueueStatus(HttpStatusCode status) =>
            _responses.Enqueue(new HttpResponseMessage(status));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string? body = null;
            if (request.Content is not null)
            {
#if NETSTANDARD2_0 || NETSTANDARD2_1
                body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
#else
                body = await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
#endif
            }

            Requests.Add(
                new CapturedRequest(request.Method, request.RequestUri!.PathAndQuery, body)
            );
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued scripted response.");
            }

            return _responses.Dequeue();
        }
    }
}
