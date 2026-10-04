using System.Net;
using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Source.Http;

namespace Configlue.Tests;

/// <summary>GET/status/ETag protocol contract for <see cref="HttpStateReader{TFragment}"/>.</summary>
public sealed class HttpStateReaderGetTests
{
    private static readonly string RevA = new('a', 64);
    private static readonly string RevB = new('b', 64);

    [Test]
    public async Task GetOk_ReturnsSuccessWithRevision()
    {
        var body = Serialize(new AppSettings.Fragment { RetryCount = Optional<int>.Present(7) });
        using var client = StubClient(_ => JsonResponse(HttpStatusCode.OK, body, RevA));
        var reader = CreateReader(client);

        var result = await reader.ReadAsync(ConfiglueResourceContext.Default);
        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe(RevA);
        result.Value!.RetryCount.Value.ShouldBe(7);
    }

    [Test]
    public async Task GetNotFound_MapsToNotFound()
    {
        using var client = StubClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var reader = CreateReader(client);

        var result = await reader.ReadAsync(ConfiglueResourceContext.Default);
        result.Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task GetBadRequestAndUnprocessable_MapsToInvalidPayload()
    {
        using var badClient = StubClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var bad = await CreateReader(badClient).ReadAsync(ConfiglueResourceContext.Default);
        bad.Status.ShouldBe(StateReadStatus.InvalidPayload);

        using var invalidClient = StubClient(_ => new HttpResponseMessage((HttpStatusCode)422));
        var invalid = await CreateReader(invalidClient)
            .ReadAsync(ConfiglueResourceContext.Default);
        invalid.Status.ShouldBe(StateReadStatus.InvalidPayload);
    }

    [Test]
    public async Task GetServerErrorAndTransportFailure_MapsToUnavailable()
    {
        using var errorClient = StubClient(_ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError
        ));
        var error = await CreateReader(errorClient).ReadAsync(ConfiglueResourceContext.Default);
        error.Status.ShouldBe(StateReadStatus.Unavailable);

        using var transportClient = StubClient(_ => throw new HttpRequestException("boom"));
        var transport = await CreateReader(transportClient)
            .ReadAsync(ConfiglueResourceContext.Default);
        transport.Status.ShouldBe(StateReadStatus.Unavailable);
    }

    [Test]
    public async Task GetWeakEtag_NormalizesToNullRevision()
    {
        var body = Serialize(new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) });
        using var client = StubClient(_ =>
        {
            var response = JsonResponse(HttpStatusCode.OK, body, RevA);
            response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(
                $"W/\"{RevA}\""
            );
            return response;
        });
        var reader = CreateReader(client);

        var result = await reader.ReadAsync(ConfiglueResourceContext.Default);
        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBeNull();
    }

    [Test]
    public async Task GetEmptyBody_MapsToInvalidPayload()
    {
        using var client = StubClient(_ => JsonResponse(HttpStatusCode.OK, [], RevA));
        var reader = CreateReader(client);

        var result = await reader.ReadAsync(ConfiglueResourceContext.Default);
        result.Status.ShouldBe(StateReadStatus.InvalidPayload);
        result.Revision.ShouldBe(RevA);
    }

    private static HttpStateReader<AppSettings.Fragment> CreateReader(HttpClient client) =>
        new(
            client,
            new Uri("http://localhost/api/settings"),
            new Uri("http://localhost/api/settings/events"),
            watchEnabled: false
        );

    private static HttpClient StubClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder
    ) =>
        new(new StubHandler(responder)) { BaseAddress = new Uri("http://localhost") };

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
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/json"
        );
        response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(
            $"\"{revision}\""
        );
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
}
