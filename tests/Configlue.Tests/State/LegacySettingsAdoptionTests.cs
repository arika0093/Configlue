using System.Buffers;
using System.Text;
using System.Text.Json;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Provider.Yaml;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class LegacySettingsAdoptionTests
{
    [Test]
    public async Task LegacyJsonCodecReadsVersionSchemaAndSparsePresenceWithoutWriting()
    {
        var json =
            "{\"$version\":1,\"RetryCount\":0,\"NullableLabel\":null,\"$schema\":\"legacy.json\"}";
        var content = Encoding.UTF8.GetBytes(json);
        var codec = new JsonStateCodec<HistoricalSettingsV1.Fragment>(
            options: new JsonSerializerOptions
            {
                UnmappedMemberHandling = System
                    .Text
                    .Json
                    .Serialization
                    .JsonUnmappedMemberHandling
                    .Disallow,
            },
            documentLayout: new DocumentLayoutOptions { ModelId = "historical-settings" }
        );
        var sequence = new ReadOnlySequence<byte>(content);
        var fragment = codec.Deserialize(in sequence, default)!;

        (fragment.RetryCount.IsPresent).ShouldBeTrue();
        (fragment.RetryCount.Value).ShouldBe(0);
        (fragment.NullableLabel.IsPresent).ShouldBeTrue();
        (fragment.NullableLabel.Value).ShouldBeNull();
        (fragment.OldName.IsPresent).ShouldBeFalse();
        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(
            new StateSchemaMetadata("historical-settings", 1)
        );
        Encoding.UTF8.GetString(content).ShouldBe(json);
    }

    [Test]
    public void LegacyJsonCodecReadsSimpleAndEnvelopePayloadsFromSegmentedInput()
    {
        var codec = new JsonStateCodec<HistoricalSettingsV1.Fragment>();
        var documents = new[]
        {
            (
                "{\"$version\":1,\"RetryCount\":0,\"NullableLabel\":null,\"$schema\":\"legacy.json\"}",
                0
            ),
            (
                "{\"$configlue\":{\"id\":\"historical-settings\",\"version\":1},\"$value\":{\"RetryCount\":9}}",
                9
            ),
        };

        foreach (var (json, expectedRetryCount) in documents)
        {
            var content = Encoding.UTF8.GetBytes(json);
            var split = content.Length / 2;
            var first = new ByteSequenceSegment(content.AsMemory(0, split));
            var last = first.Append(content.AsMemory(split));
            var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
            var fragment = codec.Deserialize(in sequence, default)!;

            (fragment.RetryCount.IsPresent).ShouldBeTrue();
            (fragment.RetryCount.Value).ShouldBe(expectedRetryCount);
            sequence.ToArray().ShouldBe(content);
        }
    }

    [Test]
    public async Task SimpleJsonCodecWritesInlineVersionDocuments()
    {
        var codec = new JsonStateCodec<HistoricalSettingsV1.Fragment>(
            documentLayout: new DocumentLayoutOptions { ModelId = "historical-settings" }
        );
        var fragment = new HistoricalSettingsV1.Fragment
        {
            RetryCount = Optional<int>.Present(0),
            NullableLabel = Optional<string?>.Present(null),
        };
        var destination = new ArrayBufferWriter<byte>();
        codec.Serialize(
            fragment,
            destination,
            new StateCodecContext(new StateSchemaMetadata("historical-settings", 1))
        );

        using var written = JsonDocument.Parse(destination.WrittenMemory);
        (written.RootElement.GetProperty("$version").GetInt32()).ShouldBe(1);
        (written.RootElement.TryGetProperty("$configlue", out _)).ShouldBeFalse();
        (written.RootElement.TryGetProperty("$value", out _)).ShouldBeFalse();
        (written.RootElement.GetProperty("RetryCount").GetInt32()).ShouldBe(0);

        var sequence = new ReadOnlySequence<byte>(destination.WrittenMemory.ToArray());
        var reread = codec.Deserialize(in sequence, default)!;
        (reread.RetryCount.Value).ShouldBe(0);
        (reread.NullableLabel.IsPresent).ShouldBeTrue();
        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(
            new StateSchemaMetadata("historical-settings", 1)
        );
    }

    [Test]
    public async Task SchemaDispatcherAttributesSimpleDocumentVersionToItsTargetModel()
    {
        var payload = new ReadOnlySequence<byte>(
            Encoding.UTF8.GetBytes("{\"$version\":1,\"RetryCount\":4}")
        );
        var codec = new JsonStateCodec<HistoricalSettingsV1.Fragment>();
        var sourceSchema = codec.ReadSchemaMetadata(in payload)!.Value;
        var targetSchema = HistoricalSettings.ConfiglueSchema.ToMetadata();
        var dispatcher = new StateSchemaDispatcher<HistoricalSettings.Fragment>(targetSchema).Add(
            new StateSchemaMetadata(targetSchema.ModelId, 1),
            codec,
            static previous => HistoricalSettings.Fragment.FromPrevious(previous)
        );

        (sourceSchema).ShouldBe(new StateSchemaMetadata(null, 1));
        (
            dispatcher.TryDeserialize(sourceSchema, in payload, null, out var migrated)
        ).ShouldBeTrue();
        (migrated!.RetryCount.Value).ShouldBe(4);
    }

    [Test]
    public async Task LegacyJsonCodecSupportsConfiguredVersionNameAndJsonNamingPolicy()
    {
        var content = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":2,\"retryCount\":7,\"nullableLabel\":null}"
        );
        var codec = new JsonStateCodec<HistoricalSettingsV1.Fragment>(
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
            new DocumentLayoutOptions
            {
                ModelId = "historical-settings",
                VersionProperty = "schemaVersion",
            }
        );
        var sequence = new ReadOnlySequence<byte>(content);

        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(
            new StateSchemaMetadata("historical-settings", 2)
        );
        var fragment = codec.Deserialize(in sequence, default)!;
        (fragment.RetryCount.Value).ShouldBe(7);
        (fragment.NullableLabel.IsPresent).ShouldBeTrue();
        (fragment.NullableLabel.Value).ShouldBeNull();
        (fragment.OldName.IsPresent).ShouldBeFalse();
    }

    [Test]
    public void LegacyJsonCodecStripsCaseInsensitiveVersionProperty()
    {
        var content = new ReadOnlySequence<byte>(
            Encoding.UTF8.GetBytes("{\"$VERSION\":3,\"RetryCount\":7}")
        );
        var codec = new JsonStateCodec<HistoricalSettingsV1.Fragment>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        (codec.ReadSchemaMetadata(in content)).ShouldBe(new StateSchemaMetadata(null, 3));
        (codec.Deserialize(in content, default)!.RetryCount.Value).ShouldBe(7);
    }

    [Test]
    public async Task UnmarkedLegacyJsonDefaultsToVersionOneAndStripsSchemaReference()
    {
        var json = "{\"$schema\":\"legacy.json\",\"RetryCount\":3}";
        var content = Encoding.UTF8.GetBytes(json);
        var codec = new JsonStateCodec<HistoricalSettingsV1.Fragment>(
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = System
                    .Text
                    .Json
                    .Serialization
                    .JsonUnmappedMemberHandling
                    .Disallow,
            },
            new DocumentLayoutOptions { ModelId = "historical-settings" }
        );
        var sequence = new ReadOnlySequence<byte>(content);

        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(
            new StateSchemaMetadata("historical-settings", 1)
        );
        var fragment = codec.Deserialize(in sequence, default)!;
        (fragment.RetryCount.Value).ShouldBe(3);
        Encoding.UTF8.GetString(content).ShouldBe(json);

        var fallbackContent = new ReadOnlySequence<byte>(
            Encoding.UTF8.GetBytes("{\"Version\":2,\"RetryCount\":4}")
        );
        (codec.ReadSchemaMetadata(in fallbackContent)).ShouldBe(
            new StateSchemaMetadata("historical-settings", 2)
        );
        (codec.Deserialize(in fallbackContent, default)!.RetryCount.Value).ShouldBe(4);
    }

    [Test]
    public async Task LegacyYamlCodecReadsVersionFallbackAndSparsePresence()
    {
        var content = Encoding.UTF8.GetBytes(
            "Version: 1\nretryCount: 0\nnullableLabel: null\n$schema: legacy.yaml\n"
        );
        var codec = new YamlStateCodec<HistoricalSettingsV1.Fragment>(
            namingPolicy: JsonNamingPolicy.CamelCase,
            modelSchema: HistoricalSettingsV1.FragmentSchema,
            documentLayout: new DocumentLayoutOptions { ModelId = "historical-settings" }
        );
        var sequence = new ReadOnlySequence<byte>(content);
        var fragment = codec.Deserialize(in sequence, default)!;

        (fragment.RetryCount.IsPresent).ShouldBeTrue();
        (fragment.RetryCount.Value).ShouldBe(0);
        (fragment.NullableLabel.IsPresent).ShouldBeTrue();
        (fragment.NullableLabel.Value).ShouldBeNull();
        (fragment.OldName.IsPresent).ShouldBeFalse();
        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(
            new StateSchemaMetadata("historical-settings", 1)
        );
        var destination = new ArrayBufferWriter<byte>();
        codec.Serialize(
            fragment,
            destination,
            new StateCodecContext(new StateSchemaMetadata("historical-settings", 1))
        );
        var written = Encoding.UTF8.GetString(destination.WrittenMemory.ToArray());
        (written.Contains("$version: 1", StringComparison.Ordinal)).ShouldBeTrue();
        (written.Contains("$configlue", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public async Task LegacyYamlCodecAcceptsEmptyInputAndBomDetectedEncodings()
    {
        var codec = new YamlStateCodec<AppSettings.Fragment>(
            namingPolicy: JsonNamingPolicy.CamelCase,
            modelSchema: AppSettings.FragmentSchema,
            documentLayout: new DocumentLayoutOptions { ModelId = "app-settings" }
        );
        foreach (var content in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("  \r\n") })
        {
            var sequence = new ReadOnlySequence<byte>(content);
            var empty = codec.Deserialize(in sequence, default)!;
            (empty.Enabled.IsPresent).ShouldBeFalse();
            (empty.RetryCount.IsPresent).ShouldBeFalse();
            (codec.ReadSchemaMetadata(in sequence)).ShouldBeNull();
        }

        foreach (var encoding in new Encoding[] { Encoding.UTF8, Encoding.Unicode, Encoding.UTF32 })
        {
            var text = "retryCount: 0\n";
            var content = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
            var sequence = new ReadOnlySequence<byte>(content);
            var fragment = codec.Deserialize(in sequence, default)!;
            (fragment.RetryCount.IsPresent).ShouldBeTrue();
            (fragment.RetryCount.Value).ShouldBe(0);
        }
    }

    [Test]
    public async Task LegacyYamlCodecTreatsNonScalarVersionAsAbsentLikeConfigurationWritable()
    {
        var content = Encoding.UTF8.GetBytes("$version:\n  nested: value\nretryCount: 0\n");
        var sequence = new ReadOnlySequence<byte>(content);
        var codec = new YamlStateCodec<AppSettings.Fragment>(
            namingPolicy: JsonNamingPolicy.CamelCase,
            modelSchema: AppSettings.FragmentSchema,
            documentLayout: new DocumentLayoutOptions { ModelId = "app-settings" }
        );

        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(
            new StateSchemaMetadata("app-settings", 1)
        );
        var fragment = codec.Deserialize(in sequence, default)!;
        (fragment.RetryCount.IsPresent).ShouldBeTrue();
        (fragment.RetryCount.Value).ShouldBe(0);
    }

    [Test]
    public async Task LegacyYamlCodecUsesExplicitNamingPolicy()
    {
        var content = Encoding.UTF8.GetBytes("retry_count: 6\nnullable_label: value\n");
        var sequence = new ReadOnlySequence<byte>(content);
        var codec = new YamlStateCodec<HistoricalSettingsV1.Fragment>(
            namingPolicy: JsonNamingPolicy.SnakeCaseLower,
            modelSchema: HistoricalSettingsV1.FragmentSchema,
            documentLayout: new DocumentLayoutOptions { ModelId = "historical-settings" }
        );

        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(
            new StateSchemaMetadata("historical-settings", 1)
        );
        var fragment = codec.Deserialize(in sequence, default)!;
        (fragment.RetryCount.Value).ShouldBe(6);
        (fragment.NullableLabel.Value).ShouldBe("value");
    }

    [Test]
    public async Task LegacyYamlSectionReadsExplicitEncodingAndLeavesOriginalBytesUntouched()
    {
        var encoding = Encoding.Unicode;
        var original = encoding
            .GetPreamble()
            .Concat(encoding.GetBytes("App:\n  Settings:\n    retryCount: 2\n  Other: keep\n"))
            .ToArray();
        var resource = new InMemoryResource();
        await resource.WriteAsync(new ResourceWriteRequest(original));
        var section = new YamlSectionResource(
            resource,
            writer: null,
            sectionPath: "App:Settings",
            textEncoding: encoding
        );
        var codec = new YamlStateCodec<AppSettings.Fragment>(
            namingPolicy: JsonNamingPolicy.CamelCase,
            modelSchema: AppSettings.FragmentSchema,
            documentLayout: new DocumentLayoutOptions { ModelId = "app-settings" },
            textEncoding: encoding
        );
        var source = new StateSource<AppSettings.Fragment>("legacy-yaml", new SerializedSource<AppSettings.Fragment>(section, codec, writer: (IResourceReader)section as IResourceWriter, watcher: (IResourceReader)section as ISourceWatcher), new StateSourceOptions<AppSettings.Fragment>());

        var result = await source.Reader.ReadAsync();
        (result.Status).ShouldBe(StateReadStatus.Success);
        (result.Value!.RetryCount.IsPresent).ShouldBeTrue();
        (result.Value.RetryCount.Value).ShouldBe(2);
        (await resource.ReadAsync()).Content.ToArray().ShouldBe(original);
    }

    [Test]
    public async Task EmptyYamlSectionIsNotFoundAndCanBeInitializedWithoutLosingSparseShape()
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("  \r\n")));
        var section = new YamlSectionResource(resource, "App:Settings");

        (await section.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);
        await section.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes("RetryCount: 0\n"))
        );

        var sectionResult = await section.ReadAsync();
        (sectionResult.Status).ShouldBe(StateReadStatus.Success);
        var codec = new YamlStateCodec<AppSettings.Fragment>(
            modelSchema: AppSettings.FragmentSchema
        );
        var content = new ReadOnlySequence<byte>(sectionResult.Content);
        var fragment = codec.Deserialize(in content, default)!;
        (fragment.RetryCount.IsPresent).ShouldBeTrue();
        (fragment.RetryCount.Value).ShouldBe(0);
        (fragment.Label.IsPresent).ShouldBeFalse();
    }

    [Test]
    public async Task LegacyJsonSectionDispatchesAndMigratesOnlyTheSelectedContribution()
    {
        var original = Encoding.UTF8.GetBytes(
            "{\"Profile\":{\"RetryCount\":0,\"NullableLabel\":null,\"OldName\":\"legacy\"},\"Other\":{\"RetryCount\":99}}"
        );
        var resource = new InMemoryResource();
        await resource.WriteAsync(new ResourceWriteRequest(original));
        var section = new JsonSectionResource(resource, "Profile");
        var currentSchema = HistoricalSettings.ConfiglueSchema.ToMetadata();
        var legacyModelId = currentSchema.ModelId!;
        var v1Codec = new JsonStateCodec<HistoricalSettingsV1.Fragment>(
            documentLayout: new DocumentLayoutOptions { ModelId = legacyModelId }
        );
        var v2Codec = new JsonStateCodec<HistoricalSettingsV2.Fragment>(
            documentLayout: new DocumentLayoutOptions { ModelId = legacyModelId }
        );
        var dispatcher = new StateSchemaDispatcher<HistoricalSettings.Fragment>(currentSchema)
            .Add(
                new StateSchemaMetadata(legacyModelId, 1),
                v1Codec,
                static previous =>
                {
                    var builder = HistoricalSettings.Fragment.FromPrevious(previous).ToBuilder();
                    if (previous.OldName.IsPresent)
                    {
                        builder.NewName = previous.OldName;
                    }
                    return builder.Build();
                }
            )
            .Add(
                new StateSchemaMetadata(legacyModelId, 2),
                v2Codec,
                static previous => HistoricalSettings.Fragment.FromPrevious(previous)
            );
        var currentCodec = new JsonStateCodec<HistoricalSettings.Fragment>(
            documentLayout: new DocumentLayoutOptions { ModelId = legacyModelId }
        );
        var source = new StateSource<HistoricalSettings.Fragment>("legacy-profile", new SerializedSource<HistoricalSettings.Fragment>(section, currentCodec, schemaDispatcher: dispatcher, writer: (IResourceReader)section as IResourceWriter, watcher: (IResourceReader)section as ISourceWatcher), new StateSourceOptions<HistoricalSettings.Fragment>());
        var targetStore = new InMemoryStateSource<HistoricalSettings.Fragment>();
        var target = new StateSource<HistoricalSettings.Fragment>("current-settings", targetStore, new StateSourceOptions<HistoricalSettings.Fragment> { Writer = targetStore });
        var higherPriorityStore = new InMemoryStateSource<HistoricalSettings.Fragment>(
            new HistoricalSettings.Fragment { RetryCount = Optional<int>.Present(99) }
        );
        var higherPrioritySource = new StateSource<HistoricalSettings.Fragment>("runtime-override", higherPriorityStore, new StateSourceOptions<HistoricalSettings.Fragment> { Priority = 500 });
        var options = new ConfiglueRuntime<HistoricalSettings, HistoricalSettings.Fragment>(
            new StateSourceSet<HistoricalSettings.Fragment>([higherPrioritySource, source, target]),
            StateWritePlan.DefaultTo(SourceId.From("current-settings"))
        );

        var result = await source.Reader.ReadAsync();
        (result.Status).ShouldBe(StateReadStatus.Success);
        (result.Schema).ShouldBe(currentSchema);
        (result.Value!.RetryCount.IsPresent).ShouldBeTrue();
        (result.Value.RetryCount.Value).ShouldBe(0);
        (result.Value.NullableLabel.IsPresent).ShouldBeTrue();
        (result.Value.NullableLabel.Value).ShouldBeNull();
        (result.Value.NewName.Value).ShouldBe("legacy");
        await options.MigrateSourceAsync(
            SourceId.From("legacy-profile"),
            SourceId.From("current-settings")
        );
        await options.MigrateSourceAsync(
            SourceId.From("legacy-profile"),
            SourceId.From("current-settings")
        );
        var migrated = (await targetStore.ReadAsync()).Value!;
        (migrated.RetryCount.Value).ShouldBe(0);
        (migrated.NullableLabel.IsPresent).ShouldBeTrue();
        (migrated.NullableLabel.Value).ShouldBeNull();
        (migrated.NewName.Value).ShouldBe("legacy");
        (await resource.ReadAsync()).Content.ToArray().ShouldBe(original);
    }

    [Test]
    public async Task LegacyCodecsRejectInvalidVersionsAndMalformedYaml()
    {
        var jsonCodec = new JsonStateCodec<AppSettings.Fragment>(
            documentLayout: new DocumentLayoutOptions { ModelId = "app-settings" }
        );
        foreach (var version in new[] { "0", "-1", "1.5", "\"one\"" })
        {
            var sequence = new ReadOnlySequence<byte>(
                Encoding.UTF8.GetBytes($"{{\"$version\":{version},\"RetryCount\":1}}")
            );
            Should.Throw<JsonException>(() => jsonCodec.ReadSchemaMetadata(in sequence));
        }

        var yamlCodec = new YamlStateCodec<AppSettings.Fragment>(
            modelSchema: AppSettings.FragmentSchema,
            documentLayout: new DocumentLayoutOptions { ModelId = "app-settings" }
        );
        foreach (var version in new[] { "0", "-1", "1.5", "one", "true", "null" })
        {
            var invalidVersion = new ReadOnlySequence<byte>(
                Encoding.UTF8.GetBytes($"$version: {version}\nretryCount: 1\n")
            );
            Should.Throw<SharpYaml.YamlException>(() =>
                yamlCodec.ReadSchemaMetadata(in invalidVersion)
            );
        }
        var malformed = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("a: [\n"));
        Should.Throw<SharpYaml.YamlException>(() => yamlCodec.Deserialize(in malformed, default));
    }

    private sealed class ByteSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public ByteSequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public ByteSequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new ByteSequenceSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };
            Next = next;
            return next;
        }
    }
}
