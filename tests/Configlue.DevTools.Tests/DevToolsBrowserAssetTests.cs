using System.Net;

namespace Configlue.DevTools.Tests;

/// <summary>
/// Proves the real loopback app (not bUnit JSInterop) delivers every asset
/// the Monaco integration needs: Blazor/BlazorMonaco scripts, the Configlue
/// bridge, and the DevTools stylesheet, with correct token gating.
/// </summary>
public sealed class DevToolsBrowserAssetTests
{
    [Test]
    public async Task Document_ReferencesEveryMonacoAsset()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = Configlue.DevTools.Web.ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        using var response = await client.GetAsync(host.Url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();

        // Blazor runtime plus the full BlazorMonaco/Monaco chain.
        html.ShouldContain("_framework/blazor.web.js");
        html.ShouldContain("_content/BlazorMonaco/jsInterop.js");
        html.ShouldContain("_content/BlazorMonaco/lib/monaco-editor/min/vs/loader.js");
        html.ShouldContain("_content/BlazorMonaco/lib/monaco-editor/min/vs/editor/editor.main.js");
        // Configlue bridge and overlay stylesheet (token-gated script/link).
        html.ShouldContain("configlue-devtools-monaco.js?token=");
        html.ShouldContain("configlue-devtools.css?token=");
    }

    [Test]
    public async Task BridgeScript_ServesRealProvidersBehindTheGate()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = Configlue.DevTools.Web.ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var anonymous = new HttpClient();
        using (var denied = await anonymous.GetAsync(host.Url + "configlue-devtools-monaco.js"))
        {
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var client = AuthedClient(host);
        using var script = await client.GetAsync(
            host.Url
                + "configlue-devtools-monaco.js?token="
                + Uri.EscapeDataString(host.SessionToken)
        );
        script.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await script.Content.ReadAsStringAsync();
        text.ShouldContain("registerHoverProvider");
        text.ShouldContain("registerInlayHintsProvider");
        text.ShouldContain("ensureDocumentModel");
    }

    [Test]
    public async Task StyleSheet_ServesAllOverlayClassesBehindTheGate()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = Configlue.DevTools.Web.ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var anonymous = new HttpClient();
        using (var denied = await anonymous.GetAsync(host.Url + "configlue-devtools.css"))
        {
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var client = AuthedClient(host);
        using var response = await client.GetAsync(
            host.Url + "configlue-devtools.css?token=" + Uri.EscapeDataString(host.SessionToken)
        );
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/css");
        var css = await response.Content.ReadAsStringAsync();
        foreach (
            var required in new[]
            {
                ".configlue-effective",
                ".configlue-secret",
                ".configlue-muted",
                ".configlue-invalid",
                ".configlue-glyph-secret",
                ".configlue-glyph-readonly",
                ".configlue-glyph-invalid",
            }
        )
        {
            css.ShouldContain(required);
        }
    }

    [Test]
    public async Task BlazorMonacoLibraryAssets_ArePublicButStillLoopbackOnly()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = Configlue.DevTools.Web.ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        // Plain script/chunk/worker loads carry no token by construction.
        using var anonymous = new HttpClient();
        using var interop = await anonymous.GetAsync(
            host.Url + "_content/BlazorMonaco/jsInterop.js"
        );
        interop.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await interop.Content.ReadAsStringAsync()).ShouldContain("blazorMonaco");

        using var loader = await anonymous.GetAsync(
            host.Url + "_content/BlazorMonaco/lib/monaco-editor/min/vs/loader.js"
        );
        loader.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The gate still refuses non-loopback remotes and unknown _content
        // trees stay gated: only the versioned BlazorMonaco path is public.
        Configlue
            .DevTools.Web.ConfiglueDevToolsTokenGateMiddleware.IsPublicAsset(
                "/_content/BlazorMonaco/jsInterop.js"
            )
            .ShouldBeTrue();
        Configlue
            .DevTools.Web.ConfiglueDevToolsTokenGateMiddleware.IsPublicAsset(
                "/_content/SomeOtherPackage/asset.js"
            )
            .ShouldBeFalse();
        Configlue
            .DevTools.Web.ConfiglueDevToolsTokenGateMiddleware.IsPublicAsset(
                "/configlue-devtools-monaco.js"
            )
            .ShouldBeFalse();
    }

    [Test]
    public async Task ServedAssets_NeverContainSecretPlaintext()
    {
        const string password = "browser-asset-pw-11";
        await using var context = DevToolsFixtures.CreateSecretContext(password);
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsSecretSettings>());
        await using var host = Configlue.DevTools.Web.ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        using var document = await client.GetAsync(host.Url);
        (await document.Content.ReadAsStringAsync()).ShouldNotContain(password);
        using var script = await client.GetAsync(host.Url + "configlue-devtools-monaco.js");
        (await script.Content.ReadAsStringAsync()).ShouldNotContain(password);
        using var css = await client.GetAsync(host.Url + "configlue-devtools.css");
        (await css.Content.ReadAsStringAsync()).ShouldNotContain(password);
    }

    private static HttpClient AuthedClient(Configlue.DevTools.Web.ConfiglueDevToolsWebHost host)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Configlue-DevTools-Token", host.SessionToken);
        return client;
    }
}
