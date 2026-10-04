using Configlue;

namespace Configlue.DevTools.Tests;

public sealed class ViewerHostTests
{
    [Test]
    public async Task ViewerDelta_ServesCanonicalJsonWithProvenance()
    {
        await using var context = ViewerHostFixtures.CreateViewerContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsViewerSettings>());
        registry.TryGet("devtools-viewer", string.Empty, out var entry).ShouldBeTrue();

        var delta = await entry!.GetViewerDeltaAsync(-1, null, CancellationToken.None);
        delta.JsonOmitted.ShouldBeFalse();
        var document = delta.Document;
        document.DocumentVersion.ShouldBeGreaterThan(0);
        document.ModelId.ShouldBe("devtools-viewer");
        document.Json.ShouldContain("Dark");
        document.Json.ShouldContain("\"Theme\"");
        document.MemberRanges.Count.ShouldBeGreaterThan(0);
        document.Decorations.ShouldNotBeNull();
        document.Hovers.ShouldNotBeNull();
        document.SchemaUri.ShouldContain("configlue://schemas/devtools-viewer");
    }

    [Test]
    public async Task ViewerDelta_OmitsJsonForDecorationOnlyDelta()
    {
        await using var context = ViewerHostFixtures.CreateViewerContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsViewerSettings>());
        registry.TryGet("devtools-viewer", string.Empty, out var entry).ShouldBeTrue();

        var first = await entry!.GetViewerDeltaAsync(-1, null, CancellationToken.None);
        var known = first.Document.DocumentVersion;

        // Nothing changed: the same text is already on the client.
        var second = await entry.GetViewerDeltaAsync(known, null, CancellationToken.None);
        second.JsonOmitted.ShouldBeTrue();
        second.Document.Json.ShouldBe(string.Empty);
        second.Document.Decorations.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task ViewerSchemaSetup_IsStablePerModelSchema()
    {
        await using var context = ViewerHostFixtures.CreateViewerContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsViewerSettings>());
        registry.TryGet("devtools-viewer", string.Empty, out var entry).ShouldBeTrue();

        var first = entry!.GetViewerSchemaSetup(null);
        var second = entry.GetViewerSchemaSetup(null);
        first.SchemaUri.ShouldBe(second.SchemaUri);
        first.SchemaUri.ShouldContain("configlue://schemas/devtools-viewer");
        first.SchemaJson.ShouldBe(second.SchemaJson);
        first.SchemaJson.ShouldContain("x-configlue-secret");
    }

    [Test]
    public async Task ViewerContribution_RedactsSecrets()
    {
        const string password = "host-pw-88";
        await using var context = ViewerHostFixtures.CreateSecretViewerContext(password);
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsViewerSettings>());
        registry.TryGet("devtools-viewer", string.Empty, out var entry).ShouldBeTrue();

        var contribution = await entry!.GetContributionJsonAsync(
            "Database.Password",
            null,
            CancellationToken.None
        );
        contribution.ShouldContain(ConfiglueSecrets.RedactedText);
        contribution.ShouldNotContain(password);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await entry.GetContributionJsonAsync(string.Empty, null, CancellationToken.None)
        );
    }

    [Test]
    public async Task ViewerDocuments_NeverLeakSecretPlaintext()
    {
        const string password = "host-leak-check-31";
        await using var context = ViewerHostFixtures.CreateSecretViewerContext(password);
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsViewerSettings>());
        registry.TryGet("devtools-viewer", string.Empty, out var entry).ShouldBeTrue();

        var delta = await entry!.GetViewerDeltaAsync(-1, null, CancellationToken.None);
        delta.Document.Json.ShouldNotContain(password);

        var setup = entry.GetViewerSchemaSetup(null);
        setup.SchemaJson.ShouldNotContain(password);
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
