using System.Net;
using System.Net.Sockets;
using System.Text;
using Configlue;
using Configlue.DevTools.Web;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.DevTools.Tests;

public sealed class DevToolsWebHostTests
{
    [Test]
    public async Task DisabledHostBindsNothing()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());

        var port = GetFreePort();
        var host = ConfiglueDevToolsWebHost.Create(
            registry,
            new ConfiglueDevToolsWebOptions { Port = port }
        );
        host.IsRunning.ShouldBeFalse();
        host.Url.ShouldBe(string.Empty);
        host.LaunchUrl.ShouldBe(string.Empty);

        // No background work when disabled: the requested port is still free.
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, port))
        {
            probe.Start();
            probe.Stop();
        }

        host.Dispose();
        host.Dispose();
    }

    [Test]
    public async Task DefaultBindingIsLoopbackWithTokenGatedDocument()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);

        host.SessionToken.Length.ShouldBeGreaterThanOrEqualTo(16);
        await host.StartAsync();
        host.IsRunning.ShouldBeTrue();
        host.Url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal).ShouldBeTrue();
        host.LaunchUrl.ShouldContain("token=");

        using var client = new HttpClient();
        using (var denied = await client.GetAsync(host.Url))
        {
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var allowed = await client.GetAsync(
            host.Url + "?token=" + Uri.EscapeDataString(host.SessionToken)
        );
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await allowed.Content.ReadAsStringAsync();
        html.ShouldContain("blazor.web.js");
        html.ShouldContain("__CONFIGLUE_DEVTOOLS_TOKEN__");
        // Interactive content renders after the circuit connects
        // (prerendering is off); the static document carries the title,
        // the boot scripts, and the page-memory token bootstrap.
        html.ShouldContain("Configlue DevTools (development only)");
        html.ShouldNotContain("localStorage");
        html.ShouldNotContain("sessionStorage");
        // The Blazor document carries no state payload and no legacy transport.
        html.ShouldNotContain("/api/");
        html.ShouldNotContain("<textarea");
    }

    [Test]
    public async Task SessionTokenIsAcceptedViaHeaderOrBearer()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var headerClient = new HttpClient();
        headerClient.DefaultRequestHeaders.Add("X-Configlue-DevTools-Token", host.SessionToken);
        using (var viaHeader = await headerClient.GetAsync(host.Url))
        {
            viaHeader.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var bearerClient = new HttpClient();
        bearerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", host.SessionToken);
        using (var viaBearer = await bearerClient.GetAsync(host.Url))
        {
            viaBearer.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var wrongClient = new HttpClient();
        wrongClient.DefaultRequestHeaders.Add("X-Configlue-DevTools-Token", "wrong");
        using (var wrong = await wrongClient.GetAsync(host.Url))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Test]
    public async Task CircuitNegotiationRequiresTheSessionToken()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var anonymous = new HttpClient();
        using (
            var denied = await anonymous.PostAsync(
                host.Url + "_blazor/negotiate?negotiateVersion=1",
                new StringContent(string.Empty)
            )
        )
        {
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var authed = AuthedClient(host);
        using var negotiated = await authed.PostAsync(
            host.Url + "_blazor/negotiate?negotiateVersion=1",
            new StringContent(string.Empty)
        );
        // The endpoint is mapped behind the gate: negotiation rejects the empty
        // payload, but it is not "forbidden" and not "not found".
        negotiated.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
        negotiated.StatusCode.ShouldNotBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task BridgeScriptIsServedBehindTheGate()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
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
        (await script.Content.ReadAsStringAsync()).ShouldContain("configlueDevToolsMonaco");

        // Shared framework boot assets stay ungated (identical for every app,
        // no state); the token-gated document plus the in-page bootstrap still
        // authorize every circuit request.
        using var framework = await anonymous.GetAsync(host.Url + "_framework/blazor.web.js");
        framework.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task NoLegacyRestTransportIsServed()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        foreach (
            var path in new[]
            {
                "api/states",
                "api/state?model=devtools-demo&name=",
                "api/schema?model=devtools-demo&name=",
                "api/diagnostics?model=devtools-demo&name=",
                "api/stats?model=devtools-demo&name=",
                "api/events?model=devtools-demo&name=",
                "api/viewer?model=devtools-demo&name=",
            }
        )
        {
            using var response = await client.GetAsync(host.Url + path);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var save = await client.PostAsync(
            host.Url + "api/save?model=devtools-demo&name=",
            new StringContent(
                /*lang=json,strict*/
                """{"Label":"unchanged","RetryCount":3}""",
                Encoding.UTF8,
                "application/json"
            )
        );
        save.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task InteractiveServerIsRegisteredWithTheLiveRegistry()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        var services = host.Services;
        services.ShouldNotBeNull();
        // The exact live registry instance is registered directly into the host.
        services!.GetRequiredService<ConfiglueDevToolsRegistry>().ShouldBeSameAs(registry);

        // InteractiveServer circuit services exist only when
        // AddInteractiveServerComponents ran for the host.
        var accessorType = Type.GetType(
            "Microsoft.AspNetCore.Components.Server.Circuits.ICircuitAccessor, Microsoft.AspNetCore.Components.Server",
            throwOnError: true
        )!;
        services.GetService(accessorType).ShouldNotBeNull();
    }

    [Test]
    public async Task StartupShutdownAndDisposalAreDeterministic()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();
        var url = host.Url;
        var token = host.SessionToken;

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Configlue-DevTools-Token", token);
        using (var document = await client.GetAsync(url))
        {
            document.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await Should.ThrowAsync<InvalidOperationException>(async () => await host.StartAsync());

        await host.StopAsync();
        host.IsRunning.ShouldBeFalse();
        host.Url.ShouldBe(string.Empty);
        await Should.ThrowAsync<HttpRequestException>(async () => await client.GetAsync(url));

        await host.StartAsync();
        var restartedUrl = host.Url;
        restartedUrl.StartsWith("http://127.0.0.1:", StringComparison.Ordinal).ShouldBeTrue();
        using (var restarted = await client.GetAsync(restartedUrl))
        {
            // A restart may select a different loopback port; the host reports the current one.
            restarted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await host.DisposeAsync();
        await host.DisposeAsync();
        host.Dispose();
    }

    [Test]
    public async Task NamedAndDynamicStatesResolveThroughTheLiveRegistry()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsNamedSettings>(model =>
        {
            model.StateName = "a";
            model.Sources(sources =>
                sources.Add(
                    DevToolsFixtures.MemorySource(
                        DevToolsNamedSettings.Fragment.From(
                            new DevToolsNamedSettings { Slot = "alpha" }
                        )
                    )
                )
            );
        });
        builder.Add<DevToolsNamedSettings>(model =>
        {
            model.StateName = "b";
            model.Sources(sources =>
                sources.Add(
                    DevToolsFixtures.MemorySource(
                        DevToolsNamedSettings.Fragment.From(
                            new DevToolsNamedSettings { Slot = "gamma" }
                        )
                    )
                )
            );
        });
        builder.Add<DevToolsDynamicSettings>(model =>
        {
            model.EnableDynamicStates = true;
            model.Sources(sources =>
                sources.Add(
                    DevToolsFixtures.MemorySource(
                        DevToolsDynamicSettings.Fragment.From(
                            new DevToolsDynamicSettings { Mood = "calm" }
                        )
                    )
                )
            );
        });
        await using var context = builder.CreateContext();
        var states = context.GetStateRegistry<DevToolsDynamicSettings>();
        (await states.TryAddAsync("extra")).ShouldBeTrue();

        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsNamedSettings>("a"), "a");
        registry.Add(context.GetState<DevToolsNamedSettings>("b"), "b");
        registry.AddRegistry(states);
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        registry.States.Count.ShouldBe(3);
        registry.States.Select(static info => info.DisplayName).ShouldContain("devtools-named:a");
        registry.States.ShouldContain(static info => info.StateName == "extra");

        registry.TryGet("devtools-named", "a", out _).ShouldBeTrue();
        registry.TryGet("devtools-named", "b", out _).ShouldBeTrue();
        registry.TryGet("devtools-dynamic", "extra", out _).ShouldBeTrue();
        registry.TryGet("devtools-demo", "nope", out _).ShouldBeFalse();
    }

    [Test]
    public async Task SecretPlaintextNeverReachesTheDocument()
    {
        const string password = "s3cr3t-pw-9z";
        await using var context = DevToolsFixtures.CreateSecretContext(password);
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsSecretSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        using var response = await client.GetAsync(
            host.Url + "?token=" + Uri.EscapeDataString(host.SessionToken)
        );
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.ShouldNotContain(password);
    }

    [Test]
    public void NoNewInspectionApiIsIntroduced()
    {
        var abstraction = typeof(ConfiglueModelAttribute).Assembly;
        abstraction.GetType("Configlue.IConfiglueInspection`1").ShouldBeNull();
        foreach (var type in abstraction.GetExportedTypes())
        {
            foreach (var method in type.GetMethods())
            {
                method.Name.ShouldNotBe("InspectAsync");
            }
        }

        foreach (
            var type in typeof(ConfiglueApp)
                .Assembly.GetExportedTypes()
                .Concat(typeof(ConfiglueDevToolsRegistry).Assembly.GetExportedTypes())
                .Concat(typeof(ConfiglueDevToolsWebHost).Assembly.GetExportedTypes())
        )
        {
            foreach (var method in type.GetMethods())
            {
                method.Name.ShouldNotBe("InspectAsync");
            }
        }
    }

    [Test]
    public async Task UnavailableSourcesReportCheckWithoutThrowing()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsDemoSettings>(model =>
            model.Sources(sources =>
                sources.Add(
                    new StateSource<DevToolsDemoSettings.Fragment>(
                        "down",
                        new DevToolsFixtures.UnavailableReader<DevToolsDemoSettings.Fragment>(),
                        new StateSourceOptions<DevToolsDemoSettings.Fragment>()
                    )
                )
            )
        );
        await using var context = builder.CreateContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        registry.TryGet("devtools-demo", string.Empty, out var entry).ShouldBeTrue();

        var check = await entry!.RunCheckAsync(CancellationToken.None);
        check.ShouldContain("Unavailable");
    }

    [Test]
    public async Task DependencyInjectionRegistrationRemainsExplicit()
    {
        await using var context = DevToolsFixtures.CreateDemoContext(label: "di");
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());

        var services = new ServiceCollection();
        services.AddConfiglueDevToolsWeb(registry);
        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<ConfiglueDevToolsWebHost>();
        await host.StartAsync();
        using var client = AuthedClient(host);
        using var document = await client.GetAsync(host.Url);
        document.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await document.Content.ReadAsStringAsync()).ShouldContain("blazor.web.js");
        await host.StopAsync();
    }

    private static HttpClient AuthedClient(ConfiglueDevToolsWebHost host)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Configlue-DevTools-Token", host.SessionToken);
        return client;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
