using System.Text.Json.Nodes;
using Bunit;
using Configlue.DevTools.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Configlue.DevTools.Tests;

/// <summary>
/// Behavior tests for the #263 Monaco integration: explicit document URIs,
/// C# projection/bridge call contracts, gated/ungated asset delivery, and
/// secret hygiene.
/// Monaco model/schema/hover/inlay/marker behavior is owned by the real
/// browser suite (<c>tests/Configlue.DevTools.Browser.Tests</c>); these C#
/// tests intentionally avoid parsing JS/CSS source for implementation tokens.
/// </summary>
public sealed class MonacoIntegrationTests : IDisposable
{
    private readonly BunitContext _context = new();

    [Test]
    public void DocumentUri_UsesExplicitStateAddressDistinctFromSchemaUri()
    {
        var documentUri = ConfiglueDevToolsViewerProjection.BuildDocumentUri(
            "devtools-viewer",
            string.Empty
        );
        documentUri.ShouldBe("configlue://states/devtools-viewer/-");

        var named = ConfiglueDevToolsViewerProjection.BuildDocumentUri("devtools-named", "second");
        named.ShouldBe("configlue://states/devtools-named/second");

        var schemaUri = ConfiglueDevToolsViewerProjection.BuildSchemaUri(
            DevToolsViewerSettings.ConfiglueSchema
        );
        schemaUri.ShouldStartWith("configlue://schemas/");

        // The language-service fileMatch must target the document URI, never
        // the schema URI and never an anonymous model URI.
        documentUri.ShouldNotBe(schemaUri);
        documentUri.ShouldNotContain("schemas");
        documentUri.ShouldNotContain("inmemory");
        named.ShouldNotBe(
            ConfiglueDevToolsViewerProjection.BuildDocumentUri("devtools-viewer", string.Empty)
        );
    }

    [Test]
    public async Task Bridge_PublishesDocumentUriNeverSchemaUriAsDocumentKey()
    {
        await using var context = CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var schema = DevToolsViewerSettings.ConfiglueSchema;
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            schema,
            null,
            1
        );
        var setup = ConfiglueDevToolsViewerProjection.BuildSchemaSetup(schema, null);
        var documentUri = ConfiglueDevToolsViewerProjection.BuildDocumentUri(
            document.ModelId,
            string.Empty
        );

        var js = new RecordingJSRuntime();
        await ConfiglueMonacoBridge.ConfigureJsonSchemaAsync(js, setup, documentUri);
        await ConfiglueMonacoBridge.EnsureDocumentModelAsync(js, "editor-1", documentUri);
        await ConfiglueMonacoBridge.SetHoverDataAsync(
            js,
            documentUri,
            document.Hovers,
            document.MemberRanges
        );
        await ConfiglueMonacoBridge.SetInlayLabelsAsync(js, documentUri, document.Decorations);
        await ConfiglueMonacoBridge.SetRuntimeMarkersAsync(js, documentUri, document.Markers);
        await ConfiglueMonacoBridge.ClearDocumentAsync(js, documentUri);

        var schemaCall = js.Calls.Single(call =>
            call.Identifier == "configlueDevToolsMonaco.setJsonSchema"
        );
        // fileMatch (third argument) binds the schema to the document model.
        schemaCall.Args[0].ShouldBe(setup.SchemaUri);
        schemaCall.Args[2].ShouldBe(documentUri);
        documentUri.ShouldNotBe(setup.SchemaUri);

        var modelCall = js.Calls.Single(call =>
            call.Identifier == "configlueDevToolsMonaco.ensureDocumentModel"
        );
        modelCall.Args[0].ShouldBe("editor-1");
        modelCall.Args[1].ShouldBe(documentUri);

        foreach (
            var identifier in new[]
            {
                "configlueDevToolsMonaco.setHoverData",
                "configlueDevToolsMonaco.setInlayLabels",
                "configlueDevToolsMonaco.setRuntimeMarkers",
                "configlueDevToolsMonaco.clearDocument",
            }
        )
        {
            var call = js.Calls.Single(call => call.Identifier == identifier);
            call.Args[0].ShouldBe(documentUri);
        }

        // Hover payloads carry the member value ranges so the provider can
        // resolve positions without rescanning the document.
        var hoverCall = js.Calls.Single(call =>
            call.Identifier == "configlueDevToolsMonaco.setHoverData"
        );
        hoverCall.Args.Length.ShouldBe(3);
    }

    [Test]
    public async Task BridgePayloads_NeverCarrySecretPlaintext()
    {
        const string password = "monaco-integration-pw-7";
        await using var context = CreateSecretViewerContext(password);
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var schema = DevToolsViewerSettings.ConfiglueSchema;
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            schema,
            null,
            1
        );
        var setup = ConfiglueDevToolsViewerProjection.BuildSchemaSetup(schema, null);
        var documentUri = ConfiglueDevToolsViewerProjection.BuildDocumentUri(
            document.ModelId,
            string.Empty
        );

        document.Json.ShouldNotContain(password);
        setup.SchemaJson.ShouldNotContain(password);
        foreach (var hover in document.Hovers)
        {
            hover.Markdown.ShouldNotContain(password);
        }
        foreach (var decoration in document.Decorations)
        {
            decoration.Label.ShouldNotContain(password);
        }
        foreach (var marker in document.Markers)
        {
            marker.Message.ShouldNotContain(password);
        }

        var js = new RecordingJSRuntime();
        await ConfiglueMonacoBridge.ConfigureJsonSchemaAsync(js, setup, documentUri);
        await ConfiglueMonacoBridge.SetHoverDataAsync(
            js,
            documentUri,
            document.Hovers,
            document.MemberRanges
        );
        await ConfiglueMonacoBridge.SetInlayLabelsAsync(js, documentUri, document.Decorations);
        await ConfiglueMonacoBridge.SetRuntimeMarkersAsync(js, documentUri, document.Markers);
        foreach (var call in js.Calls)
        {
            System.Text.Json.JsonSerializer.Serialize(call.Args).ShouldNotContain(password);
        }
    }

    [Test]
    public async Task ViewerComponent_BindsSchemaAndOverlaysToTheDocumentUri()
    {
        // Loose JSInterop still records every invocation; BlazorMonaco's own
        // editor creation stays loose while the Configlue bridge calls below
        // are asserted exactly. A wrong document key fails these assertions.
        _context.JSInterop.Mode = JSRuntimeMode.Loose;

        await using var app = RegisterViewerState(_context);
        var cut = _context.Render<ConfiglueEffectiveStateViewer<DevToolsViewerSettings>>(
            parameters =>
                parameters.Add(
                    viewer => viewer.State,
                    _context.Services.GetRequiredService<IReadOnlyState<DevToolsViewerSettings>>()
                )
        );
        cut.WaitForAssertion(() => cut.Instance.IsLoaded.ShouldBeTrue());
        await cut.InvokeAsync(() => cut.Instance.SimulateEditorInitForTestsAsync());

        var expectedKey = ConfiglueDevToolsViewerProjection.BuildDocumentUri(
            "devtools-viewer",
            string.Empty
        );
        cut.WaitForAssertion(() =>
            _context
                .JSInterop.Invocations.Any(invocation =>
                    invocation.Identifier == "configlueDevToolsMonaco.setJsonSchema"
                )
                .ShouldBeTrue()
        );

        _context
            .JSInterop.Invocations.Any(invocation =>
                invocation.Identifier == "configlueDevToolsMonaco.ensureDocumentModel"
            )
            .ShouldBeTrue();

        // The schema is configured once per model/schema even when overlays
        // re-apply (editor init plus refresh paths are idempotent).
        var schemaCall = _context.JSInterop.Invocations.Single(invocation =>
            invocation.Identifier == "configlueDevToolsMonaco.setJsonSchema"
        );
        // Third argument is the fileMatch document URI, not the schema URI.
        schemaCall.Arguments[2]?.ToString().ShouldBe(expectedKey);
        schemaCall.Arguments[0]?.ToString().ShouldNotBe(expectedKey);

        foreach (
            var identifier in new[]
            {
                "configlueDevToolsMonaco.setHoverData",
                "configlueDevToolsMonaco.setInlayLabels",
                "configlueDevToolsMonaco.setRuntimeMarkers",
            }
        )
        {
            // Overlay re-application is idempotent, but every application must
            // target the document URI. A single wrong-URI call fails this.
            var matches = _context.JSInterop.Invocations.Where(invocation =>
                invocation.Identifier == identifier
            );
            matches.ShouldNotBeEmpty();
            foreach (var invocation in matches)
            {
                invocation.Arguments[0]?.ToString().ShouldBe(expectedKey);
            }
        }

        // Hover payloads carry the member value ranges so the provider can
        // resolve positions without rescanning the document.
        var hoverCall = _context.JSInterop.Invocations.First(invocation =>
            invocation.Identifier == "configlueDevToolsMonaco.setHoverData"
        );
        hoverCall.Arguments.Count.ShouldBe(3);
        _ = app;
    }

    [Test]
    public async Task EditSaveThroughHostRegistry_ChangesTheLiveState()
    {
        // Smoke-test item 8 at the host level: an edited draft committed
        // through the owned edit session changes the live Configlue state
        // served by the running loopback host's registry.
        await using var context = CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(state);
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();
        host.IsRunning.ShouldBeTrue();

        registry.TryGet("devtools-viewer", string.Empty, out _).ShouldBeTrue();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );
        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        node["Theme"] = "HostDriven";
        var sync = await session.SyncDraftAsync(node.ToJsonString());
        sync.Success.ShouldBeTrue();
        var commit = await session.CommitAsync();
        commit.Committed.ShouldBeTrue();
        (await state.GetValueAsync()).Theme.ShouldBe("HostDriven");
    }

    public void Dispose() => _context.Dispose();

    private static ConfiglueContext CreateViewerContext()
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
                            Database = Optional<DevToolsViewerDatabase.Fragment?>.Present(
                                new DevToolsViewerDatabase.Fragment
                                {
                                    Host = Optional<string>.Present("db.local"),
                                    Port = Optional<int>.Present(5432),
                                }
                            ),
                            Tags = Optional<List<string>>.Present(["web", "blue"]),
                        }
                    )
                )
            )
        );
        return builder.CreateContext();
    }

    private static ConfiglueContext CreateSecretViewerContext(string password)
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

    private static ConfiglueContext RegisterViewerState(BunitContext context)
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
        var app = builder.CreateContext();
        context.Services.AddSingleton<IReadOnlyState<DevToolsViewerSettings>>(
            app.GetState<DevToolsViewerSettings>()
        );
        return app;
    }

    private sealed class RecordingJSRuntime : IJSRuntime
    {
        public List<(string Identifier, object?[] Args)> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            return ValueTaskCompat.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args
        )
        {
            _ = cancellationToken;
            Calls.Add((identifier, args ?? []));
            return ValueTaskCompat.FromResult(default(TValue)!);
        }
    }
}
