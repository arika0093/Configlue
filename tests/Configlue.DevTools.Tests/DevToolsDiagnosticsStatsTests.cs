using System.Net;
using System.Text.Json;
using Configlue.DevTools.Web;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.DevTools.Tests;

public sealed class DevToolsDiagnosticsStatsTests
{
    [Test]
    public async Task StatsReportsOwnershipEditableSecretAndShadowedCounts()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsDemoSettings>(model =>
            model.Sources(sources =>
            {
                sources.Add(
                    new StateSource<DevToolsDemoSettings.Fragment>(
                        "base",
                        new InMemoryStateSource<DevToolsDemoSettings.Fragment>(
                            DevToolsDemoSettings.Fragment.From(
                                new DevToolsDemoSettings { Label = "base", RetryCount = 4 }
                            )
                        ),
                        new StateSourceOptions<DevToolsDemoSettings.Fragment> { Priority = 0 }
                    )
                );
                var overlayStore = new InMemoryStateSource<DevToolsDemoSettings.Fragment>(
                    new DevToolsDemoSettings.Fragment
                    {
                        RetryCount = Optional<int>.Present(8),
                    }
                );
                sources.Add(
                    new StateSource<DevToolsDemoSettings.Fragment>(
                        "overlay",
                        overlayStore,
                        new StateSourceOptions<DevToolsDemoSettings.Fragment>
                        {
                            Priority = 100,
                            Writer = overlayStore,
                        }
                    )
                );
            })
        );
        await using var context = builder.CreateContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        var stats = JsonDocument.Parse(
            await GetJsonAsync(client, host, "stats", "devtools-demo", string.Empty)
        );

        stats.RootElement.GetProperty("totalLeaves").GetInt32().ShouldBe(2);
        stats.RootElement.GetProperty("leafRule").GetString().ShouldNotBeNullOrWhiteSpace();
        stats.RootElement.GetProperty("secretLeaves").GetInt32().ShouldBe(0);
        stats.RootElement.GetProperty("editableLeaves").GetInt32().ShouldBe(2);
        // RetryCount is present in both sources; the lower-priority copy is shadowed.
        stats.RootElement.GetProperty("leavesWithShadowedContributions").GetInt32().ShouldBe(1);
        stats.RootElement.GetProperty("shadowedContributions").GetInt32().ShouldBe(1);

        var ownership = stats.RootElement.GetProperty("ownership");
        ownership.GetArrayLength().ShouldBe(3);
        var effective = ownership
            .EnumerateArray()
            .Select(static item => item.GetProperty("effectiveLeaves").GetInt32())
            .OrderBy(static value => value)
            .ToArray();
        // Two configured sources each own one leaf; model defaults own none.
        effective.ShouldBe(new[] { 0, 1, 1 });
        ownership
            .EnumerateArray()
            .First(static item => item.GetProperty("kind").GetString() == "model-defaults")
            .GetProperty("effectiveLeaves")
            .GetInt32()
            .ShouldBe(0);
    }

    [Test]
    public async Task SecretStatsCountWithoutValues()
    {
        const string password = "s3cr3t-pw-249";
        await using var context = DevToolsFixtures.CreateSecretContext(password);
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsSecretSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        foreach (var endpoint in new[] { "diagnostics", "stats", "events" })
        {
            var body = await GetJsonAsync(
                client,
                host,
                endpoint,
                "devtools-secret-demo",
                string.Empty
            );
            body.ShouldNotContain(password);
        }

        using var check = await client.PostAsync(
            host.Url + "api/check?model=devtools-secret-demo&name=&token=" + host.SessionToken,
            new StringContent(string.Empty)
        );
        (await check.Content.ReadAsStringAsync()).ShouldNotContain(password);

        var stats = JsonDocument.Parse(
            await GetJsonAsync(client, host, "stats", "devtools-secret-demo", string.Empty)
        );
        stats.RootElement.GetProperty("totalLeaves").GetInt32().ShouldBe(2);
        stats.RootElement.GetProperty("secretLeaves").GetInt32().ShouldBe(1);
        stats.RootElement.GetRawText().ShouldNotContain(password);
        stats.RootElement.GetRawText().ShouldNotContain("s3cr3t");
    }

    [Test]
    public async Task DiagnosticsRendersTopologyWithCachedStatus()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        var diagnostics = JsonDocument.Parse(
            await GetJsonAsync(client, host, "diagnostics", "devtools-demo", string.Empty)
        );

        var state = diagnostics.RootElement.GetProperty("state");
        state.GetProperty("modelId").GetString().ShouldBe("devtools-demo");
        state.GetProperty("modelVersion").GetInt32().ShouldBe(1);
        state.GetProperty("stateName").GetString().ShouldBe(string.Empty);
        state.GetProperty("subject").GetString().ShouldBe("default");

        var sources = diagnostics.RootElement.GetProperty("sources");
        sources.GetArrayLength().ShouldBe(1);
        var source = sources[0];
        source.GetProperty("priority").GetInt32().ShouldBe(100);
        source.GetProperty("canRead").GetBoolean().ShouldBeTrue();
        source.GetProperty("canWrite").GetBoolean().ShouldBeTrue();
        source.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        source.TryGetProperty("kind", out _).ShouldBeTrue();
        source.TryGetProperty("lastRead", out _).ShouldBeTrue();
        source.TryGetProperty("revisionPresent", out _).ShouldBeTrue();
        source.TryGetProperty("lastError", out _).ShouldBeTrue();

        diagnostics.RootElement.TryGetProperty("cachedNote", out _).ShouldBeTrue();
    }

    [Test]
    public async Task OpeningTabsNeverRunsActiveCheck()
    {
        var reads = 0;
        var builder = new ConfiglueBuilder();
        var store = new InMemoryStateSource<DevToolsDemoSettings.Fragment>(
            DevToolsDemoSettings.Fragment.From(new DevToolsDemoSettings { Label = "counted" })
        );
        builder.Add<DevToolsDemoSettings>(model =>
            model.Sources(sources =>
                sources.Add(
                    new StateSource<DevToolsDemoSettings.Fragment>(
                        "counted",
                        new CountingReader<DevToolsDemoSettings.Fragment>(store, () =>
                            Interlocked.Increment(ref reads)
                        ),
                        new StateSourceOptions<DevToolsDemoSettings.Fragment>
                        {
                            Priority = 100,
                            Writer = store,
                        }
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
        var before = Volatile.Read(ref reads);

        foreach (var endpoint in new[] { "diagnostics", "events" })
        {
            using var response = await client.GetAsync(
                host.Url
                    + "api/"
                    + endpoint
                    + "?model=devtools-demo&name=&token="
                    + Uri.EscapeDataString(host.SessionToken)
            );
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Cached diagnostics/events perform no source reads and never run Check().
        Volatile.Read(ref reads).ShouldBe(before);

        using var index = await client.GetAsync(
            host.Url + "?token=" + Uri.EscapeDataString(host.SessionToken)
        );
        var html = await index.Content.ReadAsStringAsync();
        // Exactly one explicit check trigger (the button handler); no polling timers.
        CountOccurrences(html, "/api/check").ShouldBe(1);
        html.ShouldContain("Run check (explicit)");
        html.ShouldContain("Cached diagnostics/statistics never run an active check");
        html.ShouldNotContain("setInterval");
        html.ShouldNotContain("setTimeout");

        using var check = await client.PostAsync(
            host.Url + "api/check?model=devtools-demo&name=&token=" + host.SessionToken,
            new StringContent(string.Empty)
        );
        check.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await check.Content.ReadAsStringAsync()).ShouldContain("Success");
        Volatile.Read(ref reads).ShouldBeGreaterThan(before);
    }

    [Test]
    public async Task RecentEventsAreBounded()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsDemoSettings>(model =>
        {
            model.Diagnostics = new ConfiglueRuntimeDiagnosticOptions { EventHistoryCapacity = 64 };
            model.Sources(sources =>
                sources.Add(
                    DevToolsFixtures.MemorySource(
                        DevToolsDemoSettings.Fragment.From(new DevToolsDemoSettings())
                    )
                )
            );
        });
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsDemoSettings>();
        for (var index = 0; index < 10; index++)
        {
            _ = await state.GetValueAsync();
        }

        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(state);
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        var events = JsonDocument.Parse(
            await GetJsonAsync(client, host, "events", "devtools-demo", string.Empty)
        );
        events.RootElement.GetProperty("maxBound").GetInt32().ShouldBe(50);
        var returned = events.RootElement.GetProperty("returned").GetInt32();
        returned.ShouldBeLessThanOrEqualTo(50);
        var items = events.RootElement.GetProperty("events");
        items.GetArrayLength().ShouldBe(returned);
        var sequences = items
            .EnumerateArray()
            .Select(static item => item.GetProperty("sequence").GetInt64())
            .ToArray();
        sequences.ShouldBe(sequences.OrderBy(static value => value).ToArray());
        foreach (var item in items.EnumerateArray())
        {
            item.TryGetProperty("kind", out _).ShouldBeTrue();
            item.TryGetProperty("timestamp", out _).ShouldBeTrue();
        }
    }

    [Test]
    public async Task EventsAreEmptyWhenHistoryDisabled()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        var events = JsonDocument.Parse(
            await GetJsonAsync(client, host, "events", "devtools-demo", string.Empty)
        );
        events.RootElement.GetProperty("returned").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task CachedStatusRefreshesAfterWriteWithoutPolling()
    {
        await using var context = DevToolsFixtures.CreateDemoContext(label: "before");
        var state = context.GetState<DevToolsDemoSettings>();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(state);
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        var before = JsonDocument.Parse(
            await GetJsonAsync(client, host, "diagnostics", "devtools-demo", string.Empty)
        );
        before.RootElement.GetProperty("state").TryGetProperty("lastWrite", out _).ShouldBeTrue();

        using var save = await client.PostAsync(
            host.Url + "api/save?model=devtools-demo&name=&token=" + host.SessionToken,
            new StringContent(
                /*lang=json,strict*/
                """{"Label":"after","RetryCount":3}""",
                System.Text.Encoding.UTF8,
                "application/json"
            )
        );
        save.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = JsonDocument.Parse(
            await GetJsonAsync(client, host, "diagnostics", "devtools-demo", string.Empty)
        );
        var lastWrite = after.RootElement.GetProperty("state").GetProperty("lastWrite");
        lastWrite.ValueKind.ShouldNotBe(JsonValueKind.Null);
        lastWrite.GetProperty("kind").GetString().ShouldBe("WriteCompleted");
    }

    [Test]
    public async Task NoNewInspectionApiIsIntroduced()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();

        using var client = AuthedClient(host);
        foreach (var endpoint in new[] { "diagnostics", "stats", "events" })
        {
            var body = await GetJsonAsync(client, host, endpoint, "devtools-demo", string.Empty);
            body.ShouldNotContain("InspectAsync");
        }
    }

    private static HttpClient AuthedClient(ConfiglueDevToolsWebHost host)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-Configlue-DevTools-Token", host.SessionToken);
        return client;
    }

    private static async Task<string> GetJsonAsync(
        HttpClient client,
        ConfiglueDevToolsWebHost host,
        string endpoint,
        string model,
        string name
    )
    {
        using var response = await client.GetAsync(
            host.Url
                + "api/"
                + endpoint
                + "?model="
                + Uri.EscapeDataString(model)
                + "&name="
                + Uri.EscapeDataString(name)
        );
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class CountingReader<T>(ISourceReader<T> inner, Func<int> onRead)
        : ISourceReader<T>
    {
        private readonly ISourceReader<T> _inner = inner;
        private readonly Func<int> _onRead = onRead;

        public ValueTask<StateReadResult<T>> ReadAsync(
            Configlue.Resources.ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = _onRead();
            return _inner.ReadAsync(context, cancellationToken);
        }
    }
}
