using Bunit;
using Configlue.DevTools.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.DevTools.Tests;

public sealed class EffectiveStateEditorComponentTests : IDisposable
{
    private readonly BunitContext _context = new();

    [Test]
    public async Task Component_LoadsEditableDraftRedacted()
    {
        const string password = "editor-component-pw-1";
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        await using var app = RegisterEditorState(_context, password);

        var cut = _context.Render<ConfiglueEffectiveStateEditor<DevToolsViewerSettings>>(
            parameters =>
                parameters.Add(
                    editor => editor.State,
                    _context.Services.GetRequiredService<IWritableState<DevToolsViewerSettings>>()
                )
        );

        cut.WaitForAssertion(() => cut.Instance.IsLoaded.ShouldBeTrue());
        var session = cut.Instance.Session!;
        session.SessionStartDocument.Json.ShouldContain(ConfiglueSecrets.RedactedText);
        session.SessionStartDocument.Json.ShouldNotContain(password);
        cut.Instance.Session!.StateIdentity.ShouldBe("devtools-viewer");
        cut.Markup.ShouldNotContain(password);
        cut.Instance.ModifiedCount.ShouldBe(0);
        cut.Instance.HasUpstreamChanges.ShouldBeFalse();
    }

    [Test]
    public async Task Component_SecretFlowClearsTransientInput()
    {
        const string password = "editor-component-pw-2";
        const string rotated = "editor-component-rotated-3";
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        await using var app = RegisterEditorState(_context, password);
        var state = _context.Services.GetRequiredService<IWritableState<DevToolsViewerSettings>>();

        var cut = _context.Render<ConfiglueEffectiveStateEditor<DevToolsViewerSettings>>(
            parameters => parameters.Add(editor => editor.State, state)
        );
        cut.WaitForAssertion(() => cut.Instance.IsLoaded.ShouldBeTrue());

        cut.FindAll("button")
            .Single(static button => button.TextContent == "Change secret")
            .Click();
        cut.Instance.IsSecretPanelOpen.ShouldBeTrue();

        cut.Find("input[placeholder='Database.Password']").Change("Database.Password");
        cut.Find("input[type='password']").Change(rotated);
        cut.FindAll("button").Single(static button => button.TextContent == "Apply secret").Click();

        cut.WaitForAssertion(() => cut.Instance.ModifiedCount.ShouldBe(1));
        // Plaintext never survives in circuit state, markup, or the Monaco draft.
        cut.Find("input[type='password']").GetAttribute("value").ShouldBe(string.Empty);
        cut.Markup.ShouldNotContain(rotated);
        cut.Instance.Session!.SessionStartDocument.Json.ShouldNotContain(rotated);
        // Staged only: the effective value moves on Save, not on staging.
        (await state.GetValueAsync()).Database!.Password.ShouldBe(password);
        _ = app;
    }

    [Test]
    public async Task Component_FailuresSurfaceWithCategory()
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        await using var app = RegisterEditorState(_context, "editor-component-pw-4");

        var cut = _context.Render<ConfiglueEffectiveStateEditor<DevToolsViewerSettings>>(
            parameters =>
                parameters.Add(
                    editor => editor.State,
                    _context.Services.GetRequiredService<IWritableState<DevToolsViewerSettings>>()
                )
        );
        cut.WaitForAssertion(() => cut.Instance.IsLoaded.ShouldBeTrue());

        // Loose JSInterop cannot supply an editor value, so Validate funnels
        // through the same categorized failure path as any other sync problem.
        cut.FindAll("button").Single(static button => button.TextContent == "Validate").Click();

        cut.WaitForAssertion(() => cut.Instance.CurrentErrors.ShouldNotBeEmpty());
        cut.Instance.CurrentErrors[0].ShouldStartWith("[");
        cut.Markup.ShouldContain(cut.Instance.CurrentErrors[0]);
        _ = app;
    }

    public void Dispose() => _context.Dispose();

    private static ConfiglueContext RegisterEditorState(BunitContext context, string password)
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
                                    Password = Optional<string>.Present(password),
                                }
                            ),
                        }
                    )
                )
            )
        );
        var app = builder.CreateContext();
        context.Services.AddSingleton<IWritableState<DevToolsViewerSettings>>(
            app.GetState<DevToolsViewerSettings>()
        );
        return app;
    }
}
