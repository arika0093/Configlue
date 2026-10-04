using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
    public async Task DefaultBindingIsLoopbackWithSafePortSelection()
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
        using var denied = await client.GetAsync(host.Url);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var allowed = await client.GetAsync(
            host.Url + "?token=" + Uri.EscapeDataString(host.SessionToken)
        );
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await allowed.Content.ReadAsStringAsync();
        html.ShouldContain("development tooling");
        html.ShouldNotContain("localStorage");
        html.ShouldNotContain("sessionStorage");
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
        using (var states = await client.GetAsync(url + "api/states"))
        {
            states.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await host.StopAsync();
        host.IsRunning.ShouldBeFalse();
        await Should.ThrowAsync<HttpRequestException>(async () =>
            await client.GetAsync(url + "api/states")
        );

        await host.StartAsync();
        var restartedUrl = host.Url;
        restartedUrl.StartsWith("http://127.0.0.1:", StringComparison.Ordinal).ShouldBeTrue();
        using (var restarted = await client.GetAsync(restartedUrl + "api/states"))
        {
            // A restart may select a different loopback port; the host reports the current one.
            restarted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await host.DisposeAsync();
        await host.DisposeAsync();
        host.Dispose();
    }

    [Test]
    public async Task DiscoveryAndSelectionServeEachBoundState()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsDemoSettings>(model =>
            model.Sources(sources =>
                sources.Add(
                    DevToolsFixtures.MemorySource(
                        DevToolsDemoSettings.Fragment.From(
                            new DevToolsDemoSettings { Label = "first", RetryCount = 1 }
                        )
                    )
                )
            )
        );
        builder.Add<DevToolsNamedSettings>(model =>
        {
            model.StateName = "second";
            model.Sources(sources =>
                sources.Add(
                    DevToolsFixtures.MemorySource(
                        DevToolsNamedSettings.Fragment.From(
                            new DevToolsNamedSettings { Slot = "beta" }
                        )
                    )
                )
            );
        });
        await using var context = builder.CreateContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        registry.Add(context.GetState<DevToolsNamedSettings>("second"), "second");
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        using var statesResponse = await client.GetAsync(host.Url + "api/states");
        statesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var statesBody = await statesResponse.Content.ReadAsStringAsync();
        statesBody.ShouldContain("devtools-demo");
        statesBody.ShouldContain("devtools-named");

        var first = await GetStateJsonAsync(client, host, "devtools-demo", string.Empty);
        first.ShouldContain("first");

        var second = await GetStateJsonAsync(client, host, "devtools-named", "second");
        second.ShouldContain("beta");

        using var unknown = await client.GetAsync(
            host.Url + "api/state?model=devtools-demo&name=nope"
        );
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task NamedStatesResolveIndependently()
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
        await using var context = builder.CreateContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsNamedSettings>("a"), "a");
        registry.Add(context.GetState<DevToolsNamedSettings>("b"), "b");
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        (await GetStateJsonAsync(client, host, "devtools-named", "a")).ShouldContain("alpha");
        (await GetStateJsonAsync(client, host, "devtools-named", "b")).ShouldContain("gamma");
    }

    [Test]
    public async Task DynamicRegistryNamesAreResolvedLive()
    {
        var builder = new ConfiglueBuilder();
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
        registry.AddRegistry(states);
        registry.States.Count.ShouldBe(1);
        registry.States[0].StateName.ShouldBe("extra");

        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();
        using var client = AuthedClient(host);
        (await GetStateJsonAsync(client, host, "devtools-dynamic", "extra")).ShouldContain("calm");
    }

    [Test]
    public async Task SecretRedactionBoundaryHoldsAcrossPayloads()
    {
        const string password = "s3cr3t-pw-9z";
        await using var context = DevToolsFixtures.CreateSecretContext(password);
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsSecretSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        var state = await GetStateJsonAsync(client, host, "devtools-secret-demo", string.Empty);
        state.ShouldContain(ConfiglueSecrets.RedactedText);
        state.ShouldNotContain(password);

        using var schemaResponse = await client.GetAsync(
            host.Url + "api/schema?model=devtools-secret-demo&name="
        );
        var schema = await schemaResponse.Content.ReadAsStringAsync();
        schema.ShouldContain("Password");
        schema.ShouldContain("true");
        schema.ShouldNotContain(password);

        using var diagnosticsResponse = await client.GetAsync(
            host.Url + "api/diagnostics?model=devtools-secret-demo&name="
        );
        diagnosticsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await diagnosticsResponse.Content.ReadAsStringAsync()).ShouldNotContain(password);
    }

    [Test]
    public async Task ReadsAndChecksDoNotMutateOnlyExplicitSaveDoes()
    {
        await using var context = DevToolsFixtures.CreateDemoContext(label: "steady");
        var state = context.GetState<DevToolsDemoSettings>();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(state);
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();
        using var client = AuthedClient(host);

        var before = await state.GetValueAsync();
        before.Label.ShouldBe("steady");

        await GetStateJsonAsync(client, host, "devtools-demo", string.Empty);
        using (
            var diagnostics = await client.GetAsync(
                host.Url + "api/diagnostics?model=devtools-demo&name="
            )
        )
        {
            diagnostics.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (
            var check = await client.PostAsync(
                host.Url + "api/check?model=devtools-demo&name=",
                new StringContent(string.Empty)
            )
        )
        {
            check.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await check.Content.ReadAsStringAsync()).ShouldContain("Success");
        }

        (await state.GetValueAsync()).Label.ShouldBe("steady");

        using var getSave = await client.GetAsync(host.Url + "api/save?model=devtools-demo&name=");
        getSave.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await state.GetValueAsync()).Label.ShouldBe("steady");

        var edit = JsonSerializer.Serialize(
            new DevToolsDemoSettings { Label = "changed", RetryCount = 9 }
        );
        using var save = await client.PostAsync(
            host.Url + "api/save?model=devtools-demo&name=",
            new StringContent(edit, Encoding.UTF8, "application/json")
        );
        var saveBody = await save.Content.ReadAsStringAsync();
        save.StatusCode.ShouldBe(HttpStatusCode.OK, saveBody);
        (await state.GetValueAsync()).Label.ShouldBe("changed");
    }

    [Test]
    public async Task RedactedSecretsArePreservedInsteadOfOverwritten()
    {
        const string password = "keep-me-77";
        await using var context = DevToolsFixtures.CreateSecretContext(password);
        var state = context.GetState<DevToolsSecretSettings>();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(state);
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();
        using var client = AuthedClient(host);

        var projected = await GetStateJsonAsync(client, host, "devtools-secret-demo", string.Empty);
        using var save = await client.PostAsync(
            host.Url + "api/save?model=devtools-secret-demo&name=",
            new StringContent(projected, Encoding.UTF8, "application/json")
        );
        save.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await state.GetValueAsync()).Password.ShouldBe(password);
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
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        using var check = await client.PostAsync(
            host.Url + "api/check?model=devtools-demo&name=",
            new StringContent(string.Empty)
        );
        check.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await check.Content.ReadAsStringAsync()).ShouldContain("Unavailable");
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
        (await GetStateJsonAsync(client, host, "devtools-demo", string.Empty)).ShouldContain("di");
        await host.StopAsync();
    }

    private static HttpClient AuthedClient(ConfiglueDevToolsWebHost host)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Configlue-DevTools-Token", host.SessionToken);
        return client;
    }

    private static async Task<string> GetStateJsonAsync(
        HttpClient client,
        ConfiglueDevToolsWebHost host,
        string model,
        string name
    )
    {
        using var response = await client.GetAsync(
            host.Url
                + "api/state?model="
                + Uri.EscapeDataString(model)
                + "&name="
                + Uri.EscapeDataString(name)
        );
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var outer = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(outer);
        return document.RootElement.GetProperty("json").GetString() ?? string.Empty;
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
