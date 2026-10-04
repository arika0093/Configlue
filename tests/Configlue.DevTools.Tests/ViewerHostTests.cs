using System.Net;
using System.Text.Json;
using Configlue;
using Configlue.DevTools.Web;

namespace Configlue.DevTools.Tests;

public sealed class ViewerHostTests
{
    [Test]
    public async Task ViewerEndpoint_ServesCanonicalJsonWithProvenance()
    {
        await using var context = ViewerHostFixtures.CreateViewerContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsViewerSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        using var response = await client.GetAsync(
            host.Url + "api/viewer?model=devtools-viewer&name="
        );
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("\"Theme\"");
        body.ShouldContain("memberRanges");
        body.ShouldContain("decorations");
        body.ShouldContain("hovers");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("documentVersion").GetInt64().ShouldBeGreaterThan(0);
        root.GetProperty("jsonOmitted").GetBoolean().ShouldBeFalse();
        (root.GetProperty("json").GetString() ?? string.Empty).ShouldContain("Dark");
    }

    [Test]
    public async Task ViewerEndpoint_OmitsJsonForDecorationOnlyDelta()
    {
        await using var context = ViewerHostFixtures.CreateViewerContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsViewerSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        var first = await GetViewerAsync(client, host, knownVersion: -1);
        var known = first.GetProperty("documentVersion").GetInt64();

        // Nothing changed: the same text is already on the client.
        var second = await GetViewerAsync(client, host, known);
        second.GetProperty("jsonOmitted").GetBoolean().ShouldBeTrue();
        second.GetProperty("json").GetString().ShouldBe(string.Empty);
        second.GetProperty("decorations").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task ViewerSchemaEndpoint_ServesSchemaSetupOnce()
    {
        await using var context = ViewerHostFixtures.CreateViewerContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsViewerSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        using var first = await client.GetAsync(
            host.Url + "api/viewer-schema?model=devtools-viewer&name="
        );
        using var second = await client.GetAsync(
            host.Url + "api/viewer-schema?model=devtools-viewer&name="
        );
        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();
        firstBody.ShouldBe(secondBody);
        firstBody.ShouldContain("configlue://schemas/devtools-viewer");
        firstBody.ShouldContain("x-configlue-secret");
    }

    [Test]
    public async Task ViewerContributionEndpoint_RedactsSecrets()
    {
        const string password = "host-pw-88";
        await using var context = ViewerHostFixtures.CreateSecretViewerContext(password);
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsViewerSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        using var response = await client.GetAsync(
            host.Url
                + "api/viewer-contribution?model=devtools-viewer&name=&member=Database.Password"
        );
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain(ConfiglueSecrets.RedactedText);
        body.ShouldNotContain(password);

        using var missing = await client.GetAsync(
            host.Url + "api/viewer-contribution?model=devtools-viewer&name="
        );
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task ViewerEndpoints_NeverLeakSecretPlaintext()
    {
        const string password = "host-leak-check-31";
        await using var context = ViewerHostFixtures.CreateSecretViewerContext(password);
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsViewerSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        var viewer = await GetViewerAsync(client, host, knownVersion: -1);
        viewer.GetRawText().ShouldNotContain(password);

        using var schema = await client.GetAsync(
            host.Url + "api/viewer-schema?model=devtools-viewer&name="
        );
        (await schema.Content.ReadAsStringAsync()).ShouldNotContain(password);
    }

    private static async Task<JsonElement> GetViewerAsync(
        HttpClient client,
        ConfiglueDevToolsWebHost host,
        long knownVersion
    )
    {
        using var response = await client.GetAsync(
            host.Url + $"api/viewer?model=devtools-viewer&name=&knownVersion={knownVersion}"
        );
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static HttpClient AuthedClient(ConfiglueDevToolsWebHost host)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Configlue-DevTools-Token", host.SessionToken);
        return client;
    }

    private static class ViewerHostFixtures
    {
        public static ConfiglueContext CreateViewerContext()
        {
            var builder = new ConfiglueBuilder();
            builder.Add<DevToolsViewerSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        DevToolsFixtures.MemorySource(
                            new DevToolsViewerSettings.Fragment
                            {
                                Theme = Optional<string>.Present("Dark"),
                                RetryCount = Optional<int>.Present(5),
                            }
                        )
                    )
                )
            );
            return builder.CreateContext();
        }

        public static ConfiglueContext CreateSecretViewerContext(string password)
        {
            var builder = new ConfiglueBuilder();
            builder.Add<DevToolsViewerSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        DevToolsFixtures.MemorySource(
                            new DevToolsViewerSettings.Fragment
                            {
                                Database = Optional<DevToolsViewerDatabase.Fragment?>.Present(
                                    new DevToolsViewerDatabase.Fragment
                                    {
                                        Password = Optional<string>.Present(password),
                                    }
                                ),
                            }
                        )
                    )
                )
            );
            return builder.CreateContext();
        }
    }
}
