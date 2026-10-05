using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Configlue.Hosting.AspNetCore;
using Configlue.Provider.Json;
using Configlue.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

/// <summary>
/// Shared PUT/PATCH HTTP contract coverage. Where both verbs share identical error
/// behavior, a single parameterized test covers both instead of duplicated bodies.
/// Verb-specific semantics (428 on PATCH, If-Match "*" rules, patch atomicity)
/// remain in <see cref="StateHttpPatchTests"/> and <see cref="StateHttpTransportTests"/>.
/// </summary>
public sealed class StateHttpWriteContractTests
{
    [Test]
    [Arguments("PUT")]
    [Arguments("PATCH")]
    public async Task StaleIfMatch_Returns412WithoutWriting(string method)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client);

        using var request = WriteRequest(
            method,
            "http://localhost/api/settings",
            ValidBody(method),
            "\"0000000000000000000000000000000000000000000000000000000000000000\""
        );
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        DeserializeFragment(await GetBodyAsync(client)).RetryCount.Value.ShouldBe(1);
        (await GetEtagAsync(client)).ShouldBe(etag);
    }

    [Test]
    [Arguments("PUT")]
    [Arguments("PATCH")]
    public async Task ValidationFailure_Returns422WithoutWriting(string method)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client);

        using var request = WriteRequest(method, "http://localhost/api/settings", InvalidBody(method), etag);
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe((HttpStatusCode)422);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        DeserializeFragment(await GetBodyAsync(client)).RetryCount.Value.ShouldBe(1);
    }

    [Test]
    [Arguments("PUT")]
    [Arguments("PATCH")]
    public async Task MalformedBody_Returns400WithoutWriting(string method)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client);

        using var request = WriteRequest(
            method,
            "http://localhost/api/settings",
            """{"op":}""",
            etag
        );
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        DeserializeFragment(await GetBodyAsync(client)).RetryCount.Value.ShouldBe(1);
    }

    [Test]
    [Arguments("PUT")]
    [Arguments("PATCH")]
    public async Task UnsupportedMediaType_Returns415(string method)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client);

        // Each verb rejects the other's media type.
        var wrongMediaType = string.Equals(method, "PUT", StringComparison.Ordinal)
            ? "application/json-patch+json"
            : "application/json";
        using var request = new HttpRequestMessage(
            string.Equals(method, "PUT", StringComparison.Ordinal) ? HttpMethod.Put : new HttpMethod("PATCH"),
            "http://localhost/api/settings"
        )
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ValidBody(method))),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(wrongMediaType);
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    private static string ValidBody(string method) =>
        string.Equals(method, "PUT", StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(
                SerializeFragment(new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) })
            )
            : """[{"op":"replace","path":"/RetryCount","value":2}]""";

    private static string InvalidBody(string method) =>
        string.Equals(method, "PUT", StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(
                SerializeFragment(new AppSettings.Fragment { RetryCount = Optional<int>.Present(999) })
            )
            : """[{"op":"replace","path":"/RetryCount","value":999}]""";

    private static HttpRequestMessage WriteRequest(string method, string url, string body, string etag)
    {
        var isPut = string.Equals(method, "PUT", StringComparison.Ordinal);
        var request = new HttpRequestMessage(isPut ? HttpMethod.Put : new HttpMethod("PATCH"), url)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            isPut ? "application/json" : "application/json-patch+json"
        );
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        return request;
    }

    private static byte[] SerializeFragment(AppSettings.Fragment fragment)
    {
        return ConfiglueFragmentJson.SerializeToCanonicalBytes(
            fragment,
            typeof(AppSettings.Fragment),
            null
        );
    }

    private static AppSettings.Fragment DeserializeFragment(byte[] json)
    {
        return (AppSettings.Fragment)
            ConfiglueFragmentJson.Deserialize(typeof(AppSettings.Fragment), json, null);
    }

    private static async Task<byte[]> GetBodyAsync(HttpClient client)
    {
        using var response = await client.GetAsync("http://localhost/api/settings");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task<string> GetEtagAsync(HttpClient client)
    {
        using var response = await client.GetAsync("http://localhost/api/settings");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag!.ToString();
    }

    private static async Task<WebApplication> StartSingleStoreAppAsync(
        InMemoryStateSource<AppSettings.Fragment> store,
        string pattern
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConfiglue(config =>
        {
            config.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "server-store",
                            store,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Writer = store,
                                Watcher = store,
                            }
                        )
                    )
                );
                model.Writes(write =>
                    write.DefaultTo(SourceKey<AppSettings>.Named("server-store"))
                );
            });
        });
        var app = builder.Build();
        app.MapConfiglueState<AppSettings>(pattern);
        await app.StartAsync();
        return app;
    }
}
