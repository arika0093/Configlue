using System.Text.Json;
using Bunit;
using Configlue.DevTools.Web;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

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
                    new DevToolsDemoSettings.Fragment { RetryCount = Optional<int>.Present(8) }
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
        registry.TryGet("devtools-demo", string.Empty, out var entry).ShouldBeTrue();

        var stats = JsonDocument.Parse(await entry!.GetStatsJsonAsync(CancellationToken.None));

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
        registry.TryGet("devtools-secret-demo", string.Empty, out var entry).ShouldBeTrue();

        foreach (
            var body in new[]
            {
                await entry!.GetDiagnosticsJsonAsync(CancellationToken.None),
                await entry.GetStatsJsonAsync(CancellationToken.None),
                await entry.GetEventsJsonAsync(CancellationToken.None),
            }
        )
        {
            body.ShouldNotContain(password);
        }

        var check = await entry.RunCheckAsync(CancellationToken.None);
        check.ShouldNotContain(password);

        var stats = JsonDocument.Parse(await entry.GetStatsJsonAsync(CancellationToken.None));
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
        registry.TryGet("devtools-demo", string.Empty, out var entry).ShouldBeTrue();

        var diagnostics = JsonDocument.Parse(
            await entry!.GetDiagnosticsJsonAsync(CancellationToken.None)
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
    public async Task OpeningTheDiagnosticsTabNeverRunsActiveCheck()
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
                        new CountingReader<DevToolsDemoSettings.Fragment>(
                            store,
                            () => Interlocked.Increment(ref reads)
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
        var before = Volatile.Read(ref reads);

        using var testContext = new BunitContext();
        testContext.JSInterop.Mode = JSRuntimeMode.Loose;
        testContext.Services.AddSingleton(registry);
        var cut = testContext.Render<ConfiglueDevToolsDiagnosticsPanel>(parameters =>
            parameters
                .Add(panel => panel.ModelId, "devtools-demo")
                .Add(panel => panel.StateName, string.Empty)
        );

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Cached status"));
        // Cached diagnostics/events perform no source reads and never run Check().
        Volatile.Read(ref reads).ShouldBe(before);
        cut.Instance.CheckCompleted.ShouldBeFalse();

        // The tab carries exactly one explicit check trigger and no polling timers.
        cut.FindAll("button")
            .Count(static button => button.TextContent == "Run check (explicit)")
            .ShouldBe(1);
        cut.Markup.ShouldContain("Run check (explicit)");
        cut.Markup.ShouldContain("Cached diagnostics/statistics never run an active check");
        cut.Markup.ShouldNotContain("setInterval");
        cut.Markup.ShouldNotContain("setTimeout");
        cut.Markup.ShouldNotContain("/api/");
        cut.Markup.ShouldNotContain("localStorage");
        cut.Markup.ShouldNotContain("sessionStorage");

        cut.FindAll("button")
            .Single(static button => button.TextContent == "Run check (explicit)")
            .Click();
        cut.WaitForAssertion(() => cut.Instance.CheckCompleted.ShouldBeTrue());
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Success"));
        Volatile.Read(ref reads).ShouldBeGreaterThan(before);
    }

    [Test]
    public async Task StatisticsLoadExplicitlyWithoutCheck()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());

        using var testContext = new BunitContext();
        testContext.JSInterop.Mode = JSRuntimeMode.Loose;
        testContext.Services.AddSingleton(registry);
        var cut = testContext.Render<ConfiglueDevToolsDiagnosticsPanel>(parameters =>
            parameters
                .Add(panel => panel.ModelId, "devtools-demo")
                .Add(panel => panel.StateName, string.Empty)
        );

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Cached status"));
        cut.Instance.StatisticsLoaded.ShouldBeFalse();
        cut.Instance.CheckCompleted.ShouldBeFalse();

        cut.FindAll("button")
            .Single(static button => button.TextContent == "Load statistics")
            .Click();
        cut.WaitForAssertion(() => cut.Instance.StatisticsLoaded.ShouldBeTrue());
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("totalLeaves"));
        cut.Instance.CheckCompleted.ShouldBeFalse();
    }

    [Test]
    public async Task DiagnosticsPanelRedactsSecrets()
    {
        const string password = "panel-pw-249b";
        await using var context = DevToolsFixtures.CreateSecretContext(password);
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsSecretSettings>());

        using var testContext = new BunitContext();
        testContext.JSInterop.Mode = JSRuntimeMode.Loose;
        testContext.Services.AddSingleton(registry);
        var cut = testContext.Render<ConfiglueDevToolsDiagnosticsPanel>(parameters =>
            parameters
                .Add(panel => panel.ModelId, "devtools-secret-demo")
                .Add(panel => panel.StateName, string.Empty)
        );

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Cached status"));
        cut.Markup.ShouldNotContain(password);
        cut.FindAll("button")
            .Single(static button => button.TextContent == "Load statistics")
            .Click();
        cut.WaitForAssertion(() => cut.Instance.StatisticsLoaded.ShouldBeTrue());
        cut.Markup.ShouldNotContain(password);
    }

    [Test]
    public async Task RecentEventsAreSnapshotDerivedAndBounded()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsDemoSettings>(model =>
        {
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
        registry.TryGet("devtools-demo", string.Empty, out var entry).ShouldBeTrue();

        var events = JsonDocument.Parse(await entry!.GetEventsJsonAsync(CancellationToken.None));
        events.RootElement.GetProperty("source").GetString().ShouldBe("snapshot");
        var returned = events.RootElement.GetProperty("returned").GetInt32();
        returned.ShouldBeLessThanOrEqualTo(50);
        returned.ShouldBeGreaterThan(0);
        var items = events.RootElement.GetProperty("events");
        items.GetArrayLength().ShouldBe(returned);
        var timestamps = items
            .EnumerateArray()
            .Select(static item => item.GetProperty("timestamp").GetDateTimeOffset())
            .ToArray();
        timestamps.ShouldBe(timestamps.OrderByDescending(static value => value).ToArray());
        foreach (var item in items.EnumerateArray())
        {
            item.TryGetProperty("kind", out _).ShouldBeTrue();
            item.TryGetProperty("timestamp", out _).ShouldBeTrue();
        }
    }

    [Test]
    public async Task EventsAreEmptyBeforeAnyOperation()
    {
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        registry.TryGet("devtools-demo", string.Empty, out var entry).ShouldBeTrue();

        var events = JsonDocument.Parse(await entry!.GetEventsJsonAsync(CancellationToken.None));
        events.RootElement.GetProperty("source").GetString().ShouldBe("snapshot");
        events.RootElement.GetProperty("returned").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task CachedStatusRefreshesAfterWriteWithoutPolling()
    {
        await using var context = DevToolsFixtures.CreateDemoContext(label: "before");
        var state = context.GetState<DevToolsDemoSettings>();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(state);
        registry.TryGet("devtools-demo", string.Empty, out var entry).ShouldBeTrue();

        var before = JsonDocument.Parse(
            await entry!.GetDiagnosticsJsonAsync(CancellationToken.None)
        );
        before.RootElement.GetProperty("state").TryGetProperty("lastWrite", out _).ShouldBeTrue();

        // The only mutating path is the semantic edit-session save.
        await entry.ApplyJsonAsync(
            /*lang=json,strict*/
            """{"Label":"after","RetryCount":3}""",
            CancellationToken.None
        );

        var after = JsonDocument.Parse(await entry.GetDiagnosticsJsonAsync(CancellationToken.None));
        var lastWrite = after.RootElement.GetProperty("state").GetProperty("lastWrite");
        lastWrite.ValueKind.ShouldNotBe(JsonValueKind.Null);
        lastWrite.GetProperty("kind").GetString().ShouldBe("WriteCompleted");
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
