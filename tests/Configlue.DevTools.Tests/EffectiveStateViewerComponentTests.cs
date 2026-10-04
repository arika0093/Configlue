using Bunit;
using Configlue;
using Configlue.DevTools.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.DevTools.Tests;

public sealed class EffectiveStateViewerComponentTests : IDisposable
{
    private readonly BunitContext _context = new();

    [Test]
    public async Task Component_LoadsCanonicalJsonThroughLifecycle()
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        await using var app = RegisterViewerState(_context, "Dark");

        var cut = _context.Render<ConfiglueEffectiveStateViewer<DevToolsViewerSettings>>(
            parameters =>
                parameters.Add(
                    state => state.State,
                    _context.Services.GetRequiredService<IReadOnlyState<DevToolsViewerSettings>>()
                )
        );

        cut.WaitForAssertion(() =>
        {
            var component = cut.Instance;
            component.IsLoaded.ShouldBeTrue();
            component.CurrentDocument.ShouldNotBeNull();
            component.CurrentDocument!.Json.ShouldContain("\"Theme\": \"Dark\"");
        });
        cut.Markup.Length.ShouldBeGreaterThan(0);
        _ = app;
    }

    [Test]
    public async Task Component_OverlaysProvenanceWithoutEditing()
    {
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
        await using var app = RegisterViewerState(_context, "Dark");

        var cut = _context.Render<ConfiglueEffectiveStateViewer<DevToolsViewerSettings>>(
            parameters =>
                parameters.Add(
                    state => state.State,
                    _context.Services.GetRequiredService<IReadOnlyState<DevToolsViewerSettings>>()
                )
        );

        cut.WaitForAssertion(() => cut.Instance.IsLoaded.ShouldBeTrue());
        var document = cut.Instance.CurrentDocument!;
        document.Decorations.ShouldNotBeEmpty();
        document.Hovers.ShouldNotBeEmpty();
        document.SchemaUri.ShouldContain("devtools-viewer");
        _ = app;
    }

    [Test]
    public async Task Component_RedactsSecretsInRenderedState()
    {
        const string password = "component-pw-64";
        _context.JSInterop.Mode = JSRuntimeMode.Loose;
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
        await using var app = builder.CreateContext();
        _context.Services.AddSingleton<IReadOnlyState<DevToolsViewerSettings>>(
            app.GetState<DevToolsViewerSettings>()
        );

        var cut = _context.Render<ConfiglueEffectiveStateViewer<DevToolsViewerSettings>>(
            parameters =>
                parameters.Add(
                    state => state.State,
                    _context.Services.GetRequiredService<IReadOnlyState<DevToolsViewerSettings>>()
                )
        );

        cut.WaitForAssertion(() => cut.Instance.IsLoaded.ShouldBeTrue());
        cut.Instance.CurrentDocument!.Json.ShouldContain(ConfiglueSecrets.RedactedText);
        cut.Instance.CurrentDocument!.Json.ShouldNotContain(password);
        cut.Markup.ShouldNotContain(password);
    }

    public void Dispose() => _context.Dispose();

    private static ConfiglueContext RegisterViewerState(BunitContext context, string theme)
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
            model.Sources(sources =>
                sources.Add(
                    DevToolsFixtures.MemorySource(
                        new DevToolsViewerSettings.Fragment
                        {
                            Theme = Optional<string>.Present(theme),
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
}
