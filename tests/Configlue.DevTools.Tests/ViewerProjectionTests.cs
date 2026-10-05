using System.Text.Json;
using System.Text.Json.Nodes;
using Configlue;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.DevTools.Tests;

public sealed class ViewerProjectionTests
{
    [Test]
    public async Task Projection_IsDeterministicAndFollowsSchemaOrder()
    {
        await using var context = CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var schema = DevToolsViewerSettings.ConfiglueSchema;

        var first = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            schema,
            null,
            1
        );
        var second = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            schema,
            null,
            2
        );

        first.Json.ShouldBe(second.Json);
        var order = schema.Members.Select(static member => member.Name).ToArray();
        var positions = order.Select(name =>
            first.Json.IndexOf($"\"{name}\"", StringComparison.Ordinal)
        );
        positions.ShouldBe(positions.OrderBy(static position => position));
        first.ModelId.ShouldBe("devtools-viewer");
        first.ModelVersion.ShouldBe(1);
    }

    [Test]
    public async Task Projection_RendersNestingCollectionsAndNulls()
    {
        await using var context = CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            DevToolsViewerSettings.ConfiglueSchema,
            null,
            1
        );

        document.Json.ShouldContain("\"Theme\": \"Dark\"");
        document.Json.ShouldContain("\"Host\": \"db.local\"");
        document.Json.ShouldContain("\"Tags\": [");
        document.Json.ShouldContain("\"Notes\": null");
        var node = JsonNode.Parse(document.Json)!.AsObject();
        node["Tags"]!.AsArray().Count.ShouldBe(2);
        node["Database"]!["Port"]!.GetValue<int>().ShouldBe(5432);

        var paths = document.MemberRanges.Select(static range => range.MemberPath).ToArray();
        paths.ShouldContain("Database.Host");
        paths.ShouldContain("Database.Port");
        paths.ShouldContain("Tags[0]");
        paths.ShouldContain("Tags[1]");
    }

    [Test]
    public async Task Projection_NullNestedModelRendersNullLiteral()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
            model.Sources(sources =>
                sources.Add(
                    DevToolsFixtures.MemorySource(
                        new DevToolsViewerSettings.Fragment
                        {
                            Database = Optional<DevToolsViewerDatabase.Fragment?>.Present(null),
                        }
                    )
                )
            )
        );
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            DevToolsViewerSettings.ConfiglueSchema,
            null,
            1
        );

        document.Json.ShouldContain("\"Database\": null");
    }

    [Test]
    public async Task Projection_HonorsCamelCaseNamingPolicy()
    {
        await using var context = CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var options = new ConfiglueDevToolsViewerOptions
        {
            NamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            DevToolsViewerSettings.ConfiglueSchema,
            options,
            1
        );

        document.Json.ShouldContain("\"retryCount\"");
        document.Json.Contains("\"RetryCount\"", StringComparison.Ordinal).ShouldBeFalse();
        var retry = document.MemberRanges.Single(static range => range.MemberPath == "RetryCount");
        retry.WireName.ShouldBe("retryCount");
    }

    [Test]
    public async Task Projection_PreservesExplicitJsonPropertyName()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerNamingSettings>(model =>
            model.Sources(sources =>
                sources.Add(
                    DevToolsFixtures.MemorySource(
                        DevToolsViewerNamingSettings.Fragment.From(
                            new DevToolsViewerNamingSettings { Theme = "Dark", RetryCount = 7 }
                        )
                    )
                )
            )
        );
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsViewerNamingSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            DevToolsViewerNamingSettings.ConfiglueSchema,
            null,
            1
        );

        document.Json.ShouldContain("\"theme\"");
        document.Json.Contains("\"Theme\"", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Test]
    public async Task RangeMapping_CoversMembersWithValidMonacoCoordinates()
    {
        await using var context = CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            DevToolsViewerSettings.ConfiglueSchema,
            null,
            1
        );

        document.MemberRanges.Count.ShouldBeGreaterThan(5);
        var lines = document.Json.Split('\n');
        foreach (var range in document.MemberRanges)
        {
            range.NameRange.IsValid.ShouldBeTrue(range.MemberPath);
            range.ValueRange.IsValid.ShouldBeTrue(range.MemberPath);
            var extracted = Extract(lines, range.ValueRange);
            extracted.Length.ShouldBeGreaterThan(0, range.MemberPath);
        }

        var theme = document.MemberRanges.Single(static range => range.MemberPath == "Theme");
        Extract(lines, theme.ValueRange).ShouldBe("\"Dark\"");
        var host = document.MemberRanges.Single(static range =>
            range.MemberPath == "Database.Host"
        );
        Extract(lines, host.ValueRange).ShouldBe("\"db.local\"");
    }

    [Test]
    public async Task Provenance_AnnotatesEffectiveSource()
    {
        await using var context = CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            DevToolsViewerSettings.ConfiglueSchema,
            null,
            1
        );

        var effective = document.Decorations.Where(static decoration =>
            decoration.Kind == ConfiglueViewerDecorationKind.Effective
        );
        effective.ShouldNotBeEmpty();
        var retry = document.Decorations.First(static decoration =>
            decoration.MemberPath == "RetryCount"
            && decoration.Kind == ConfiglueViewerDecorationKind.Effective
        );
        retry.Label.Length.ShouldBeGreaterThan(0);
        var hover = document.Hovers.Single(static hover => hover.MemberPath == "RetryCount");
        hover.Markdown.ShouldContain("Effective source:");
        hover.Markdown.ShouldContain("Contributions");
        hover.EffectiveSource.ShouldNotBeNull();
        hover.EffectiveSource!.Length.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Hover_ExplainsShadowedContributions()
    {
        await using var context = CreateShadowedContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            DevToolsViewerSettings.ConfiglueSchema,
            null,
            1
        );

        var hover = document.Hovers.Single(static hover => hover.MemberPath == "RetryCount");
        hover.Markdown.ShouldContain("Shadowed");
        hover.Markdown.ShouldContain("Effective");
        var effective = hover.Contributions.Single(static contribution => contribution.IsEffective);
        var shadowed = hover.Contributions.Where(static contribution => contribution.IsShadowed);
        shadowed.ShouldNotBeEmpty();
        shadowed
            .Select(static contribution => contribution.SourceKey)
            .ShouldNotContain(effective.SourceKey);
    }

    [Test]
    public async Task ReadOnly_MarksNonWritableEffectiveValues()
    {
        var store = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
            new DevToolsViewerSettings.Fragment { RetryCount = Optional<int>.Present(11) }
        );
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
            model.Sources(sources =>
                sources.Add(
                    new StateSource<DevToolsViewerSettings.Fragment>(
                        "sealed",
                        store,
                        new StateSourceOptions<DevToolsViewerSettings.Fragment>()
                    )
                )
            )
        );
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            DevToolsViewerSettings.ConfiglueSchema,
            null,
            1
        );

        var marker = document.Decorations.FirstOrDefault(static decoration =>
            decoration.MemberPath == "RetryCount"
            && decoration.Kind == ConfiglueViewerDecorationKind.ReadOnly
        );
        marker.ShouldNotBeNull();
        var hover = document.Hovers.Single(static hover => hover.MemberPath == "RetryCount");
        hover.Markdown.ShouldContain("Editable: No");
    }

    [Test]
    public async Task SchemaSetup_DescribesMembersAndSecretsOncePerModel()
    {
        var schema = DevToolsViewerSettings.ConfiglueSchema;
        var first = ConfiglueDevToolsViewerProjection.BuildSchemaSetup(schema, null);
        var second = ConfiglueDevToolsViewerProjection.BuildSchemaSetup(
            schema,
            new ConfiglueDevToolsViewerOptions()
        );

        first.SchemaUri.ShouldBe(second.SchemaUri);
        first.SchemaJson.ShouldBe(second.SchemaJson);
        first.SchemaUri.ShouldContain("devtools-viewer");
        var node = JsonNode.Parse(first.SchemaJson)!.AsObject();
        var properties = node["properties"]!.AsObject();
        properties.ContainsKey("Theme").ShouldBeTrue();
        properties.ContainsKey("Database").ShouldBeTrue();
        properties["Database"]!["properties"]!["Password"]![
            ConfiglueSecrets.JsonSchemaExtensionName
        ]!
            .GetValue<bool>()
            .ShouldBeTrue();
    }

    [Test]
    public async Task RuntimeMarkers_SurfaceInvalidPayloadsWithoutSecrets()
    {
        // Issue #312: a malformed high-priority payload fails visibly instead of
        // silently falling back to the lower-priority source.
        const string password = "marker-pw-42";
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
            model.Sources(sources =>
            {
                sources.Add(
                    new StateSource<DevToolsViewerSettings.Fragment>(
                        "broken",
                        new InvalidPayloadReader<DevToolsViewerSettings.Fragment>(),
                        new StateSourceOptions<DevToolsViewerSettings.Fragment>
                        {
                            Priority = 100,
                            FallbackCondition =
                                StateFallbackCondition.NotFoundOrUnavailable,
                        }
                    )
                );
                sources.Add(
                    DevToolsFixtures.MemorySource(
                        new DevToolsViewerSettings.Fragment
                        {
                            Theme = Optional<string>.Present("Dark"),
                            Database = Optional<DevToolsViewerDatabase.Fragment?>.Present(
                                new DevToolsViewerDatabase.Fragment
                                {
                                    Password = Optional<string>.Present(password),
                                }
                            ),
                        }
                    )
                );
            })
        );
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsViewerSettings>();

        var failure = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await state.GetSnapshotAsync()
        );
        failure.Message.ShouldContain("InvalidPayload");
        failure.Message.ShouldNotContain(password);

        var check = ((IConfiglueDiagnostics<DevToolsViewerSettings>)state).Check();
        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }

        // Resolution stops at the malformed source; the lower-priority source is never consulted.
        streamed.Count.ShouldBe(1);
        streamed[0].Status.ShouldBe(ConfiglueCheckStatus.Invalid);
        streamed[0].FallbackContinued.ShouldBeFalse();
        streamed[0].Contributed.ShouldBeFalse();
        (await check.Result).Status.ShouldBe(ConfiglueCheckStatus.Invalid);
    }

    [Test]
    public async Task Secrets_UseStablePlaceholderEverywhere()
    {
        const string password = "s3cr3t-viewer-9z";
        await using var context = CreateSecretViewerContext(password);
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            DevToolsViewerSettings.ConfiglueSchema,
            null,
            1
        );

        document.Json.ShouldContain(ConfiglueSecrets.RedactedText);
        document.Json.ShouldNotContain(password);
        var passwordRange = document.MemberRanges.Single(static range =>
            range.MemberPath == "Database.Password"
        );
        passwordRange.IsSecret.ShouldBeTrue();
        var secretDecorations = document.Decorations.Where(static decoration =>
            decoration.Kind == ConfiglueViewerDecorationKind.Secret
        );
        secretDecorations.ShouldNotBeEmpty();
        foreach (var decoration in document.Decorations)
        {
            decoration.Label.ShouldNotContain(password);
        }

        var hover = document.Hovers.Single(static hover => hover.MemberPath == "Database.Password");
        hover.IsSecret.ShouldBeTrue();
        hover.Markdown.ShouldContain("Secret: Yes");
        hover.Markdown.ShouldNotContain(password);
    }

    [Test]
    public async Task Projection_LeaksNoSourceSyntax()
    {
        await using var context = CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var document = ConfiglueDevToolsViewerProjection.BuildDocument(
            snapshot.Value,
            snapshot.Details,
            DevToolsViewerSettings.ConfiglueSchema,
            null,
            1
        );

        document.Json.ShouldNotContain("MYAPP_");
        document.Json.ShouldNotContain("---");
        foreach (var line in document.Json.Split('\n'))
        {
            line.TrimStart().StartsWith("#", StringComparison.Ordinal).ShouldBeFalse();
        }

        // The text parses as JSON: no comments or source-specific syntax survived.
        JsonNode.Parse(document.Json).ShouldNotBeNull();
    }

    [Test]
    public async Task ContributionProjection_IsNormalizedJsonWithoutRawSources()
    {
        await using var context = CreateShadowedContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var snapshot = await state.GetSnapshotAsync();
        var json = ConfiglueDevToolsViewerProjection.BuildContributionJson(
            snapshot.Details!,
            DevToolsViewerSettings.ConfiglueSchema,
            "RetryCount"
        );

        var node = JsonNode.Parse(json)!.AsObject();
        node["memberPath"]!.GetValue<string>().ShouldBe("RetryCount");
        var contributions = node["contributions"]!.AsArray();
        contributions.Count.ShouldBeGreaterThanOrEqualTo(2);
        contributions
            .Any(static entry =>
                entry!["effective"]!.GetValue<bool>() && entry!["present"]!.GetValue<bool>()
            )
            .ShouldBeTrue();
        contributions.Any(static entry => entry!["shadowed"]!.GetValue<bool>()).ShouldBeTrue();
    }

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
                                    Password = Optional<string>.Present("pw-viewer"),
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

    private static ConfiglueContext CreateShadowedContext()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
            model.Sources(sources =>
            {
                sources.Add(
                    new StateSource<DevToolsViewerSettings.Fragment>(
                        "override",
                        new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
                            new DevToolsViewerSettings.Fragment
                            {
                                RetryCount = Optional<int>.Present(8),
                            }
                        ),
                        new StateSourceOptions<DevToolsViewerSettings.Fragment>
                        {
                            Priority = 100,
                            Writer = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(),
                        }
                    )
                );
                sources.Add(
                    new StateSource<DevToolsViewerSettings.Fragment>(
                        "base",
                        new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
                            new DevToolsViewerSettings.Fragment
                            {
                                RetryCount = Optional<int>.Present(4),
                            }
                        ),
                        new StateSourceOptions<DevToolsViewerSettings.Fragment>()
                    )
                );
            })
        );
        return builder.CreateContext();
    }

    private static string Extract(string[] lines, ConfiglueViewerRange range)
    {
        if (range.StartLineNumber == range.EndLineNumber)
        {
            var line = lines[range.StartLineNumber - 1];
            return line.Substring(range.StartColumn - 1, range.EndColumn - range.StartColumn);
        }

        var first = lines[range.StartLineNumber - 1].Substring(range.StartColumn - 1);
        var last = lines[range.EndLineNumber - 1].Substring(0, range.EndColumn - 1);
        var middle = lines[(range.StartLineNumber)..(range.EndLineNumber - 1)];
        return string.Join("\n", new[] { first }.Concat(middle).Concat([last]));
    }

    private sealed class InvalidPayloadReader<T> : ISourceReader<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(
            Configlue.Resources.ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(StateReadResult<T>.InvalidPayload(default));
        }
    }
}
