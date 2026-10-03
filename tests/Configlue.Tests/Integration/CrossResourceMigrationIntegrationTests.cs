using System.Buffers;
using System.Text;
using Configlue.Provider.Json;
using Configlue.Provider.Yaml;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class CrossResourceMigrationIntegrationTests
{
    [Test]
    public async Task MigrateSourceAsync_CopiesJsonFileContributionIntoYamlSectionOfAnotherResource()
    {
        var directory = CreateTempDirectory();
        var sourcePath = Path.Combine(directory, "source.json");
        var targetPath = Path.Combine(directory, "target.yaml");
        using var sourceFile = new FileResource(sourcePath);
        try
        {
            await File.WriteAllTextAsync(targetPath, "Keep:\n  Value: preserved\n");
            using var targetFile = new FileResource(targetPath);
            var jsonCodec = new JsonStateCodec<AppSettings.Fragment>(
                documentLayout: new DocumentLayoutOptions { ModelId = "app-settings" }
            );
            var yamlCodec = new YamlStateCodec<AppSettings.Fragment>(
                modelSchema: AppSettings.FragmentSchema,
                documentLayout: new DocumentLayoutOptions { ModelId = "app-settings" }
            );
            var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
                "json-file",
                sourceFile,
                jsonCodec,
                priority: 100
            );
            var targetSection = new YamlSectionResource(targetFile, "App:Settings");
            var target = SerializedStateSource.FromResource<AppSettings.Fragment>(
                "yaml-section",
                targetSection,
                yamlCodec,
                priority: 0
            );
            await source.Writer!.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment
                    {
                        RetryCount = Optional<int>.Present(7),
                        Label = Optional<string?>.Present("from-json"),
                        Database = Optional<DatabaseSettings.Fragment?>.Present(
                            new DatabaseSettings.Fragment
                            {
                                Host = Optional<string>.Present("json.db"),
                                Port = Optional<int>.Present(6432),
                            }
                        ),
                    }
                )
            );
            var sourceBefore = (await sourceFile.ReadAsync()).Content.ToArray();
            await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                new StateSourceSet<AppSettings.Fragment>([source, target]),
                StateWritePlan.DefaultTo(SourceId.From("json-file"))
            );

            var migration = await options.MigrateSourceAsync(
                SourceId.From("json-file"),
                SourceId.From("yaml-section")
            );

            var targetResult = await target.Reader.ReadAsync();
            var targetText = Encoding.UTF8.GetString((await targetFile.ReadAsync()).Content.Span);
            var independentlyRead = DeserializeYaml(
                yamlCodec,
                Encoding.UTF8.GetString((await targetSection.ReadAsync()).Content.Span)
            );
            var sourceAfter = (await sourceFile.ReadAsync()).Content.ToArray();

            (migration.SourceId).ShouldBe(SourceId.From("json-file"));
            (migration.TargetId).ShouldBe(SourceId.From("yaml-section"));
            (targetResult.Status).ShouldBe(StateReadStatus.Success);
            (targetResult.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
            (targetResult.Value!.RetryCount.Value).ShouldBe(7);
            (targetResult.Value.Label.Value).ShouldBe("from-json");
            (targetResult.Value.Database.Value!.Host.Value).ShouldBe("json.db");
            (targetResult.Value.Database.Value.Port.Value).ShouldBe(6432);
            (independentlyRead.RetryCount.Value).ShouldBe(7);
            (independentlyRead.Database.Value!.Port.Value).ShouldBe(6432);
            targetText.ShouldContain("$version: 2");
            targetText.ShouldContain("Keep:");
            targetText.ShouldNotContain("{");
            sourceAfter.ShouldBe(sourceBefore);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task MigrateSourcesToTargetsAsync_ProjectsMergedSerializedSourcesIntoCodecSpecificTargets()
    {
        var directory = CreateTempDirectory();
        var jsonSourcePath = Path.Combine(directory, "source.json");
        var yamlTargetPath = Path.Combine(directory, "target.yaml");
        using var jsonSourceFile = new FileResource(jsonSourcePath);
        using var yamlTargetFile = new FileResource(yamlTargetPath);
        try
        {
            var jsonCodec = new JsonStateCodec<AppSettings.Fragment>(
                documentLayout: new DocumentLayoutOptions { ModelId = "app-settings" }
            );
            var yamlCodec = new YamlStateCodec<AppSettings.Fragment>(
                modelSchema: AppSettings.FragmentSchema,
                documentLayout: new DocumentLayoutOptions { ModelId = "app-settings" }
            );
            var jsonSource = SerializedStateSource.FromResource<AppSettings.Fragment>(
                "json-file",
                jsonSourceFile,
                jsonCodec,
                priority: 100
            );
            var yamlSourceResource = new InMemoryResource();
            var yamlSource = SerializedStateSource.FromResource<AppSettings.Fragment>(
                "yaml-memory",
                yamlSourceResource,
                yamlCodec,
                priority: 50
            );
            await jsonSource.Writer!.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment
                    {
                        RetryCount = Optional<int>.Present(11),
                        Label = Optional<string?>.Present("json-label"),
                    }
                )
            );
            await yamlSource.Writer!.WriteAsync(
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment
                    {
                        Database = Optional<DatabaseSettings.Fragment?>.Present(
                            new DatabaseSettings.Fragment
                            {
                                Host = Optional<string>.Present("yaml.db"),
                                Port = Optional<int>.Present(5432),
                            }
                        ),
                        Plugins = Optional<IReadOnlyList<string>>.Present(["yaml-plugin"]),
                    }
                )
            );
            var jsonBefore = (await jsonSourceFile.ReadAsync()).Content.ToArray();
            var jsonTargetResource = new InMemoryResource();
            var jsonTarget = SerializedStateSource.FromResource<AppSettings.Fragment>(
                "json-target",
                jsonTargetResource,
                jsonCodec,
                priority: 0
            );
            var yamlTarget = SerializedStateSource.FromResource<AppSettings.Fragment>(
                "yaml-target",
                yamlTargetFile,
                yamlCodec,
                priority: -1
            );
            await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                new StateSourceSet<AppSettings.Fragment>([
                    jsonSource,
                    yamlSource,
                    jsonTarget,
                    yamlTarget,
                ]),
                StateWritePlan.DefaultTo(SourceId.From("json-file"))
            );
            var projections = new Dictionary<
                SourceId,
                Func<AppSettings.Fragment, AppSettings.Fragment>
            >()
            {
                [SourceId.From("json-target")] = static fragment => fragment,
                [SourceId.From("yaml-target")] = static fragment => new AppSettings.Fragment
                {
                    RetryCount = fragment.RetryCount,
                },
            };

            var firstRun = await options.MigrateSourcesToTargetsAsync(
                [SourceId.From("json-file"), SourceId.From("yaml-memory")],
                projections
            );
            var jsonTargetResult = await jsonTarget.Reader.ReadAsync();
            var yamlTargetResult = await yamlTarget.Reader.ReadAsync();
            var jsonTargetText = Encoding.UTF8.GetString(
                (await jsonTargetResource.ReadAsync()).Content.Span
            );
            var yamlTargetText = Encoding.UTF8.GetString(
                (await yamlTargetFile.ReadAsync()).Content.Span
            );
            var independentlyReadJson = DeserializeJson(jsonCodec, jsonTargetText);
            var independentlyReadYaml = DeserializeYaml(yamlCodec, yamlTargetText);

            (firstRun.Targets.All(static result => !result.WasAlreadyCurrent)).ShouldBeTrue();
            (jsonTargetResult.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
            (jsonTargetResult.Value!.RetryCount.Value).ShouldBe(11);
            (jsonTargetResult.Value.Label.Value).ShouldBe("json-label");
            (jsonTargetResult.Value.Database.Value!.Host.Value).ShouldBe("yaml.db");
            (jsonTargetResult.Value.Database.Value.Port.Value).ShouldBe(5432);
            jsonTargetResult.Value.Plugins.Value!.ShouldContain("yaml-plugin");
            (independentlyReadJson.Database.Value!.Port.Value).ShouldBe(5432);
            (yamlTargetResult.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
            (yamlTargetResult.Value!.RetryCount.Value).ShouldBe(11);
            (yamlTargetResult.Value.Label.IsPresent).ShouldBeFalse();
            (independentlyReadYaml.RetryCount.Value).ShouldBe(11);
            jsonTargetText.ShouldContain("{");
            yamlTargetText.ShouldContain("$version: 2");
            yamlTargetText.ShouldNotContain("{");

            var secondRun = await options.MigrateSourcesToTargetsAsync(
                [SourceId.From("json-file"), SourceId.From("yaml-memory")],
                projections
            );

            (secondRun.Targets.All(static result => result.WasAlreadyCurrent)).ShouldBeTrue();
            (await jsonSourceFile.ReadAsync()).Content.ToArray().ShouldBe(jsonBefore);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task MigrateSourceAsync_MigratesHistoricalJsonContributionIntoCurrentYamlTarget()
    {
        var currentSchema = HistoricalSettings.ConfiglueSchema.ToMetadata();
        var legacyCodec = new JsonStateCodec<HistoricalSettingsV1.Fragment>(
            documentLayout: new DocumentLayoutOptions { ModelId = currentSchema.ModelId }
        );
        var legacyFragment = new HistoricalSettingsV1.Fragment
        {
            RetryCount = Optional<int>.Present(7),
            NullableLabel = Optional<string?>.Present(null),
            OldName = Optional<string?>.Present("legacy-name"),
        };
        var sourceResource = new InMemoryResource();
        var legacyBytes = new ArrayBufferWriter<byte>();
        legacyCodec.Serialize(legacyFragment, legacyBytes, default);
        var sourceBefore = legacyBytes.WrittenMemory.ToArray();
        await sourceResource.WriteAsync(new ResourceWriteRequest(sourceBefore));
        var dispatcher = new StateSchemaDispatcher<HistoricalSettings.Fragment>(currentSchema).Add(
            new StateSchemaMetadata(currentSchema.ModelId, 1),
            legacyCodec,
            static previous =>
            {
                var builder = HistoricalSettings.Fragment.FromPrevious(previous).ToBuilder();
                builder.NewName.CopyFrom(previous.OldName);
                return builder.Build();
            }
        );
        var source = SerializedStateSource.FromResource<HistoricalSettings.Fragment>(
            "legacy-json",
            sourceResource,
            new JsonStateCodec<HistoricalSettings.Fragment>(
                documentLayout: new DocumentLayoutOptions { ModelId = currentSchema.ModelId }
            ),
            priority: 100,
            schemaDispatcher: dispatcher
        );
        var targetResource = new InMemoryResource();
        var yamlCodec = new YamlStateCodec<HistoricalSettings.Fragment>(
            modelSchema: HistoricalSettings.FragmentSchema,
            documentLayout: new DocumentLayoutOptions { ModelId = currentSchema.ModelId }
        );
        var target = SerializedStateSource.FromResource<HistoricalSettings.Fragment>(
            "current-yaml",
            targetResource,
            yamlCodec,
            priority: 0
        );
        await using var options = new ConfiglueRuntime<
            HistoricalSettings,
            HistoricalSettings.Fragment
        >(
            new StateSourceSet<HistoricalSettings.Fragment>([source, target]),
            StateWritePlan.DefaultTo(SourceId.From("legacy-json"))
        );

        await options.MigrateSourceAsync(
            SourceId.From("legacy-json"),
            SourceId.From("current-yaml")
        );

        var targetResult = await target.Reader.ReadAsync();
        var targetText = Encoding.UTF8.GetString((await targetResource.ReadAsync()).Content.Span);
        var independentlyRead = DeserializeYaml(yamlCodec, targetText);

        (targetResult.Status).ShouldBe(StateReadStatus.Success);
        (targetResult.Schema).ShouldBe(currentSchema);
        (targetResult.Value!.RetryCount.Value).ShouldBe(7);
        (targetResult.Value.NullableLabel.IsPresent).ShouldBeTrue();
        (targetResult.Value.NullableLabel.Value).ShouldBeNull();
        (targetResult.Value.NewName.Value).ShouldBe("legacy-name");
        (independentlyRead.NewName.Value).ShouldBe("legacy-name");
        targetText.ShouldContain("$version: 3");
        targetText.ShouldNotContain("{");
        (await sourceResource.ReadAsync()).Content.ToArray().ShouldBe(sourceBefore);
    }

    [Test]
    public async Task MigrateSourceAsync_VerifiesTheTargetRetainedTheMigratedFragment()
    {
        var source = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var target = new NonRetainingStateTarget<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("stale") }
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("source", source, priority: 100),
                new("target", target, priority: 0, writer: target),
            ]),
            StateWritePlan.DefaultTo(SourceId.From("source"))
        );

        var rejected = false;
        try
        {
            await options.MigrateSourceAsync(SourceId.From("source"), SourceId.From("target"));
        }
        catch (StateConflictException)
        {
            rejected = true;
        }

        (rejected).ShouldBeTrue();
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ConfiglueCrossResourceMigration",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static AppSettings.Fragment DeserializeJson(
        JsonStateCodec<AppSettings.Fragment> codec,
        string text
    )
    {
        var sequence = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(text));
        return codec.Deserialize(in sequence, default)!;
    }

    private static AppSettings.Fragment DeserializeYaml(
        YamlStateCodec<AppSettings.Fragment> codec,
        string text
    )
    {
        var sequence = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(text));
        return codec.Deserialize(in sequence, default)!;
    }

    private static HistoricalSettings.Fragment DeserializeYaml(
        YamlStateCodec<HistoricalSettings.Fragment> codec,
        string text
    )
    {
        var sequence = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(text));
        return codec.Deserialize(in sequence, default)!;
    }

    private sealed class NonRetainingStateTarget<T>(T retained) : ISourceReader<T>, ISourceWriter<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<StateReadResult<T>>(StateReadResult<T>.Success(retained, "1"));
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<StateWriteResult>(new StateWriteResult("1"));
        }
    }
}
