using System.Buffers;
using System.Text;
using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Provider.Yaml;
using Configlue.Testing;
using YamlDotNet.Core;

namespace Configlue.Tests;

public sealed class LegacySettingsAdoptionTests
{
    [Test]
    public async Task LegacyJsonCodecReadsVersionSchemaAndSparsePresenceWithoutWriting()
    {
        var json =
            "{\"$version\":1,\"RetryCount\":0,\"NullableLabel\":null,\"$schema\":\"legacy.json\"}";
        var content = Encoding.UTF8.GetBytes(json);
        var codec = new ConfigurationWritableJsonStateCodec<HistoricalSettingsV1.Fragment>(
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = System
                    .Text
                    .Json
                    .Serialization
                    .JsonUnmappedMemberHandling
                    .Disallow,
            },
            modelId: "historical-settings"
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
        Should.Throw<NotSupportedException>(() => codec.Serialize(fragment, destination, default));
        Encoding.UTF8.GetString(content).ShouldBe(json);
    }

    [Test]
    public async Task LegacyJsonCodecSupportsConfiguredVersionNameAndJsonNamingPolicy()
    {
        var content = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":2,\"retryCount\":7,\"nullableLabel\":null}"
        );
        var codec = new ConfigurationWritableJsonStateCodec<HistoricalSettingsV1.Fragment>(
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
            modelId: "historical-settings",
            versionProperty: "schemaVersion"
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
    public async Task UnmarkedLegacyJsonDefaultsToVersionOneAndStripsSchemaReference()
    {
        var json = "{\"$schema\":\"legacy.json\",\"RetryCount\":3}";
        var content = Encoding.UTF8.GetBytes(json);
        var codec = new ConfigurationWritableJsonStateCodec<HistoricalSettingsV1.Fragment>(
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = System
                    .Text
                    .Json
                    .Serialization
                    .JsonUnmappedMemberHandling
                    .Disallow,
            },
            modelId: "historical-settings"
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
        var codec = new ConfigurationWritableYamlStateCodec<HistoricalSettingsV1.Fragment>(
            modelId: "historical-settings"
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
        Should.Throw<NotSupportedException>(() => codec.Serialize(fragment, destination, default));
    }

    [Test]
    public async Task LegacyYamlCodecAcceptsEmptyInputAndBomDetectedEncodings()
    {
        var codec = new ConfigurationWritableYamlStateCodec<AppSettings.Fragment>(
            modelId: "app-settings"
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
        var codec = new ConfigurationWritableYamlStateCodec<AppSettings.Fragment>(
            modelId: "app-settings"
        );

        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(
            new StateSchemaMetadata("app-settings", 1)
        );
        var fragment = codec.Deserialize(in sequence, default)!;
        (fragment.RetryCount.IsPresent).ShouldBeTrue();
        (fragment.RetryCount.Value).ShouldBe(0);
    }

    [Test]
    public async Task LegacyYamlCodecUsesExplicitNamingConvention()
    {
        var content = Encoding.UTF8.GetBytes("retry_count: 6\nnullable_label: value\n");
        var sequence = new ReadOnlySequence<byte>(content);
        var codec = new ConfigurationWritableYamlStateCodec<HistoricalSettingsV1.Fragment>(
            namingConvention: YamlDotNet
                .Serialization
                .NamingConventions
                .UnderscoredNamingConvention
                .Instance,
            modelId: "historical-settings"
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
        var codec = new ConfigurationWritableYamlStateCodec<AppSettings.Fragment>(
            modelId: "app-settings",
            encoding: encoding
        );
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "legacy-yaml",
            section,
            codec
        );

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
        var codec = new YamlStateCodec<AppSettings.Fragment>();
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
        var v1Codec = new ConfigurationWritableJsonStateCodec<HistoricalSettingsV1.Fragment>(
            modelId: legacyModelId
        );
        var v2Codec = new ConfigurationWritableJsonStateCodec<HistoricalSettingsV2.Fragment>(
            modelId: legacyModelId
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
        var currentCodec = new ConfigurationWritableJsonStateCodec<HistoricalSettings.Fragment>(
            modelId: legacyModelId
        );
        var source = SerializedStateSource.FromResource<HistoricalSettings.Fragment>(
            "legacy-profile",
            section,
            currentCodec,
            schemaDispatcher: dispatcher
        );
        var targetStore = new InMemoryStateStore<HistoricalSettings.Fragment>();
        var target = new StateSource<HistoricalSettings.Fragment>(
            "current-settings",
            targetStore,
            writer: targetStore
        );
        var higherPriorityStore = new InMemoryStateStore<HistoricalSettings.Fragment>(
            new HistoricalSettings.Fragment { RetryCount = Optional<int>.Present(99) }
        );
        var higherPrioritySource = new StateSource<HistoricalSettings.Fragment>(
            "runtime-override",
            higherPriorityStore,
            priority: 500
        );
        var options = new ConfiglueOptions<HistoricalSettings, HistoricalSettings.Fragment>(
            new StateSourceSet<HistoricalSettings.Fragment>([higherPrioritySource, source, target])
        );

        var result = await source.Reader.ReadAsync();
        (result.Status).ShouldBe(StateReadStatus.Success);
        (result.Schema).ShouldBe(currentSchema);
        (result.Value!.RetryCount.IsPresent).ShouldBeTrue();
        (result.Value.RetryCount.Value).ShouldBe(0);
        (result.Value.NullableLabel.IsPresent).ShouldBeTrue();
        (result.Value.NullableLabel.Value).ShouldBeNull();
        (result.Value.NewName.Value).ShouldBe("legacy");
        await options.MigrateSourceAsync("legacy-profile", "current-settings");
        await options.MigrateSourceAsync("legacy-profile", "current-settings");
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
        var jsonCodec = new ConfigurationWritableJsonStateCodec<AppSettings.Fragment>(
            modelId: "app-settings"
        );
        foreach (var version in new[] { "0", "-1", "1.5", "\"one\"" })
        {
            var sequence = new ReadOnlySequence<byte>(
                Encoding.UTF8.GetBytes($"{{\"$version\":{version},\"RetryCount\":1}}")
            );
            Should.Throw<JsonException>(() => jsonCodec.ReadSchemaMetadata(in sequence));
        }

        var yamlCodec = new ConfigurationWritableYamlStateCodec<AppSettings.Fragment>(
            modelId: "app-settings"
        );
        var invalidVersion = new ReadOnlySequence<byte>(
            Encoding.UTF8.GetBytes("$version: 0\nretryCount: 1\n")
        );
        Should.Throw<YamlException>(() => yamlCodec.ReadSchemaMetadata(in invalidVersion));
        var malformed = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("a: [\n"));
        Should.Throw<YamlException>(() => yamlCodec.Deserialize(in malformed, default));
    }
}
