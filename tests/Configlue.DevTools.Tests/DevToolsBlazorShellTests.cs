using Bunit;
using Configlue.DevTools.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.DevTools.Tests;

public sealed class DevToolsBlazorShellTests : IDisposable
{
    private readonly BunitContext _context = new();

    [Test]
    public async Task Shell_ListsAllBoundStatesAndOpensTheFirstLiveEditor()
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        await using var app = RegisterTwoStates(_context);

        var cut = _context.Render<ConfiglueDevToolsShell>();
        cut.Markup.ShouldContain("development tooling");
        cut.Markup.ShouldContain("devtools-demo");
        cut.Markup.ShouldContain("devtools-named:second");

        var editor = cut.FindComponent<ConfiglueEffectiveStateEditor<DevToolsDemoSettings>>();
        editor.Instance.StateName.ShouldBe(string.Empty);
        cut.WaitForAssertion(() => editor.Instance.IsLoaded.ShouldBeTrue());
        editor.Instance.Session!.StateIdentity.ShouldBe("devtools-demo");
        _ = app;
    }

    [Test]
    public async Task Shell_SwitchingSelectionDisposesTheSessionAndOpensTheCorrectEditor()
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        await using var app = RegisterTwoStates(_context);

        var cut = _context.Render<ConfiglueDevToolsShell>();
        var first = cut.FindComponent<ConfiglueEffectiveStateEditor<DevToolsDemoSettings>>();
        cut.WaitForAssertion(() => first.Instance.IsLoaded.ShouldBeTrue());
        var firstComponent = first.Instance;
        var firstSession = firstComponent.Session!;
        firstSession.StateIdentity.ShouldBe("devtools-demo");

        var registry = _context.Services.GetRequiredService<ConfiglueDevToolsRegistry>();
        var named = registry.States.Single(static info => info.ModelId == "devtools-named");
        cut.Find("select").Change(ConfiglueDevToolsShell.SelectionKey(named));

        // The previous editor (and its owned session) is disposed; the new
        // state opens a fresh session that can never see the stale draft.
        cut.WaitForAssertion(() => firstComponent.Session.ShouldBeNull());
        cut.FindComponents<ConfiglueEffectiveStateEditor<DevToolsDemoSettings>>().Count.ShouldBe(0);

        var second = cut.FindComponent<ConfiglueEffectiveStateEditor<DevToolsNamedSettings>>();
        second.Instance.StateName.ShouldBe("second");
        cut.WaitForAssertion(() => second.Instance.IsLoaded.ShouldBeTrue());
        second.Instance.Session!.StateIdentity.ShouldBe("devtools-named:second");
        second.Instance.Session.ShouldNotBeSameAs(firstSession);
        _ = app;
    }

    [Test]
    public async Task Shell_DynamicRegistryNamesResolveLive()
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
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
        _context.Services.AddSingleton(registry);

        var cut = _context.Render<ConfiglueDevToolsShell>();
        cut.Markup.ShouldContain("devtools-dynamic:extra");

        var editor = cut.FindComponent<ConfiglueEffectiveStateEditor<DevToolsDynamicSettings>>();
        cut.WaitForAssertion(() => editor.Instance.IsLoaded.ShouldBeTrue());
        editor.Instance.Session!.StateIdentity.ShouldBe("devtools-dynamic:extra");
    }

    [Test]
    public async Task Shell_DiagnosticsTabRendersCachedDataWithExplicitCheckOnly()
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        await using var app = RegisterTwoStates(_context);

        var cut = _context.Render<ConfiglueDevToolsShell>();
        cut.WaitForAssertion(() =>
            cut.FindComponent<ConfiglueEffectiveStateEditor<DevToolsDemoSettings>>()
                .Instance.IsLoaded.ShouldBeTrue()
        );

        cut.FindAll("button").Single(static button => button.TextContent == "Diagnostics").Click();

        var panel = cut.FindComponent<ConfiglueDevToolsDiagnosticsPanel>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Cached status"));
        panel.Instance.CheckCompleted.ShouldBeFalse();
        cut.Markup.ShouldNotContain("/api/");
        _ = app;
    }

    [Test]
    public void EditorHost_UnknownStateShowsFallbackWithoutEditor()
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        var registry = new ConfiglueDevToolsRegistry();
        _context.Services.AddSingleton(registry);

        var cut = _context.Render<ConfiglueDevToolsEditorHost>(parameters =>
            parameters
                .Add(host => host.ModelId, "no-such-model")
                .Add(host => host.StateName, string.Empty)
        );

        cut.Markup.ShouldContain("Unknown state");
        cut.Instance.ResolvedIdentity.ShouldBeNull();
        cut.FindComponents<ConfiglueEffectiveStateEditor<DevToolsDemoSettings>>().Count.ShouldBe(0);
    }

    public void Dispose() => _context.Dispose();

    private static ConfiglueContext RegisterTwoStates(BunitContext context)
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
        var app = builder.CreateContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(app.GetState<DevToolsDemoSettings>());
        registry.Add(app.GetState<DevToolsNamedSettings>("second"), "second");
        context.Services.AddSingleton(registry);
        return app;
    }
}
