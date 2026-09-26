using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Text;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;

namespace Configlue.Tests;

[ConfiglueModel(2, Id = "app-settings")]
public partial class AppSettings
{
    public bool Enabled { get; set; } = true;

    [Range(0, 100)]
    public int RetryCount { get; set; } = 3;

    public string? Label { get; set; } = "default";

    public DatabaseSettings? Database { get; set; } = new();

    [ConfiglueMerge(MergeMode.Append)]
    public IReadOnlyList<string> Plugins { get; set; } = [];
}

[ConfiglueModel(2, Id = "database-settings")]
public partial class DatabaseSettings
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;
}

[ConfiglueModel(1, Id = "set-union-settings")]
public partial class SetUnionSettings
{
    [ConfiglueMerge(MergeMode.SetUnion)]
    public IReadOnlyList<string> Tags { get; set; } = [];
}

public sealed class GeneratedFragmentTests
{
    [Test]
    public async Task SparseFragment_PreservesMissingPresentNullAndDefault()
    {
        var fragment = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Label = Optional<string?>.Present(null),
        };

        var value = fragment.ToModel();

        await Assert.That(fragment.Enabled.IsPresent).IsTrue();
        await Assert.That(fragment.Enabled.Value).IsFalse();
        await Assert.That(fragment.RetryCount.IsPresent).IsFalse();
        await Assert.That(fragment.Label.IsPresent).IsTrue();
        await Assert.That(fragment.Label.Value).IsNull();
        await Assert.That(value.Enabled).IsFalse();
        await Assert.That(value.RetryCount).IsEqualTo(3);
        await Assert.That(value.Database!.Host).IsEqualTo("localhost");
    }

    [Test]
    public async Task Merge_DeepMergesNestedModelsAndAppendsCollections()
    {
        var lower = new AppSettings.Fragment
        {
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment
                {
                    Host = Optional<string>.Present("db.local"),
                    Port = Optional<int>.Present(5432),
                }
            ),
            Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
        };
        var higher = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
            ),
            Plugins = Optional<IReadOnlyList<string>>.Present(["custom"]),
        };

        var merged = lower.Merge(higher).ToModel();

        await Assert.That(merged.Enabled).IsFalse();
        await Assert.That(merged.Database!.Host).IsEqualTo("db.local");
        await Assert.That(merged.Database.Port).IsEqualTo(6432);
        await Assert.That(merged.Plugins).IsEquivalentTo(["base", "custom"]);
    }

    [Test]
    public async Task SemanticDiffAndPatch_DistinguishSetFromUnset()
    {
        var before = new AppSettings
        {
            Enabled = true,
            RetryCount = 3,
            Label = "old",
        };
        var after = new AppSettings
        {
            Enabled = false,
            RetryCount = 3,
            Label = null,
        };

        var diff = AppSettings.Fragment.Diff(before, after);
        var patched = AppSettings
            .Fragment.From(before)
            .Apply(
                new AppSettings.Patch
                {
                    Enabled = FragmentOperation<bool>.Set(false),
                    RetryCount = FragmentOperation<int>.Unset,
                }
            );

        await Assert.That(diff.Enabled.Value).IsFalse();
        await Assert.That(diff.RetryCount.IsPresent).IsFalse();
        await Assert.That(diff.Label.IsPresent).IsTrue();
        await Assert.That(diff.Label.Value).IsNull();
        await Assert.That(patched.Enabled.Value).IsFalse();
        await Assert.That(patched.RetryCount.IsPresent).IsFalse();
        await Assert
            .That(new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) }.IsEmpty)
            .IsFalse();
        await Assert.That(AppSettings.ConfiglueSchema.Version).IsEqualTo(2);
        await Assert.That(AppSettings.ConfiglueSchema.Id).IsEqualTo("app-settings");
    }

    [Test]
    public async Task JsonCodec_RoundTripsSparsePresenceAndSchemaMetadata()
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var context = new StateCodecContext(new StateSchemaMetadata("app-settings", 2));
        var fragment = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Label = Optional<string?>.Present(null),
        };
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        codec.Serialize(fragment, buffer, in context);
        var encoded = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        var decoded = codec.Deserialize(in sequence, default);
        var decodedFragment = decoded!;
        var schema = codec.ReadSchemaMetadata(in sequence);

        await Assert.That(encoded).Contains("\"$configlue\"");
        await Assert.That(encoded).Contains("\"Label\":null");
        await Assert.That(encoded).DoesNotContain("RetryCount");
        await Assert.That(decodedFragment.Enabled.IsPresent).IsTrue();
        await Assert.That(decodedFragment.Enabled.Value).IsFalse();
        await Assert.That(decodedFragment.Label.IsPresent).IsTrue();
        await Assert.That(decodedFragment.Label.Value).IsNull();
        await Assert.That(decodedFragment.RetryCount.IsPresent).IsFalse();
        await Assert.That(schema).IsEqualTo(new StateSchemaMetadata("app-settings", 2));
    }

    [Test]
    public async Task JsonCodec_UsesGeneratedFragmentSchemaWhenContextIsOmitted()
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Serialize(
            new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) },
            buffer,
            default
        );
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);

        await Assert
            .That(codec.ReadSchemaMetadata(in sequence))
            .IsEqualTo(AppSettings.ConfiglueSchema.ToMetadata());
    }

    [Test]
    public async Task SerializedWriter_PersistsGeneratedSchemaBesideFragmentResources()
    {
        var resource = new InMemoryResource();
        var writer = new SerializedStateWriter<AppSettings.Fragment>(
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );
        var fragment = new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) };

        await writer.WriteAsync(new StateWriteRequest<AppSettings.Fragment>(fragment));
        var stored = await resource.ReadAsync();

        await Assert.That(stored.Schema).IsEqualTo(AppSettings.ConfiglueSchema.ToMetadata());
    }

    [Test]
    public async Task XmlCodec_RoundTripsSparseNestedValuesAndSchemaMetadata()
    {
        var codec = new XmlStateCodec<AppSettings.Fragment>();
        var fragment = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Label = Optional<string?>.Present(null),
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment { Host = Optional<string>.Present("db.local") }
            ),
            Plugins = Optional<IReadOnlyList<string>>.Present(["admin"]),
        };
        var buffer = new ArrayBufferWriter<byte>();
        var context = new StateCodecContext(new StateSchemaMetadata("app-settings", 2));

        codec.Serialize(fragment, buffer, in context);
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        var decoded = codec.Deserialize(in sequence, default)!;

        await Assert.That(decoded.Enabled.IsPresent).IsTrue();
        await Assert.That(decoded.Enabled.Value).IsFalse();
        await Assert.That(decoded.RetryCount.IsPresent).IsFalse();
        await Assert.That(decoded.Label.IsPresent).IsTrue();
        await Assert.That(decoded.Label.Value).IsNull();
        await Assert.That(decoded.Database.IsPresent).IsTrue();
        await Assert.That(decoded.Database.Value!.Host.Value).IsEqualTo("db.local");
        await Assert.That(decoded.Database.Value!.Port.IsPresent).IsFalse();
        await Assert.That(decoded.Plugins.Value).IsEquivalentTo(["admin"]);
        await Assert
            .That(codec.ReadSchemaMetadata(in sequence))
            .IsEqualTo(new StateSchemaMetadata("app-settings", 2));
    }

    [Test]
    public async Task YamlCodec_RoundTripsSparseNestedValuesAndSchemaMetadata()
    {
        var codec = new YamlStateCodec<AppSettings.Fragment>();
        var fragment = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Label = Optional<string?>.Present(null),
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment { Host = Optional<string>.Present("db.local") }
            ),
            Plugins = Optional<IReadOnlyList<string>>.Present(["admin"]),
        };
        var buffer = new ArrayBufferWriter<byte>();
        var context = new StateCodecContext(new StateSchemaMetadata("app-settings", 2));

        codec.Serialize(fragment, buffer, in context);
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        var decoded = codec.Deserialize(in sequence, default)!;

        await Assert.That(decoded.Enabled.IsPresent).IsTrue();
        await Assert.That(decoded.Enabled.Value).IsFalse();
        await Assert.That(decoded.RetryCount.IsPresent).IsFalse();
        await Assert.That(decoded.Label.IsPresent).IsTrue();
        await Assert.That(decoded.Label.Value).IsNull();
        await Assert.That(decoded.Database.IsPresent).IsTrue();
        await Assert.That(decoded.Database.Value!.Host.Value).IsEqualTo("db.local");
        await Assert.That(decoded.Database.Value!.Port.IsPresent).IsFalse();
        await Assert.That(decoded.Plugins.Value).IsEquivalentTo(["admin"]);
        await Assert
            .That(codec.ReadSchemaMetadata(in sequence))
            .IsEqualTo(new StateSchemaMetadata("app-settings", 2));
    }

    [Test]
    public async Task XmlAndYamlCodecs_RoundTripOrdinaryModelsWithSchemaMetadata()
    {
        var model = new AppSettings
        {
            Enabled = false,
            RetryCount = 0,
            Label = null,
            Database = new DatabaseSettings { Host = "db.local", Port = 6432 },
            Plugins = ["admin", "metrics"],
        };
        var context = new StateCodecContext(AppSettings.ConfiglueSchema.ToMetadata());
        var xml = new XmlStateCodec<AppSettings>();
        var xmlBuffer = new ArrayBufferWriter<byte>();
        xml.Serialize(model, xmlBuffer, in context);
        var xmlSequence = new ReadOnlySequence<byte>(xmlBuffer.WrittenMemory);
        var xmlModel = xml.Deserialize(in xmlSequence, default)!;

        var yaml = new YamlStateCodec<AppSettings>();
        var yamlBuffer = new ArrayBufferWriter<byte>();
        yaml.Serialize(model, yamlBuffer, in context);
        var yamlSequence = new ReadOnlySequence<byte>(yamlBuffer.WrittenMemory);
        var yamlModel = yaml.Deserialize(in yamlSequence, default)!;

        await Assert.That(xmlModel.Enabled).IsFalse();
        await Assert.That(xmlModel.RetryCount).IsEqualTo(0);
        await Assert.That(xmlModel.Label).IsNull();
        await Assert.That(xmlModel.Database!.Port).IsEqualTo(6432);
        await Assert.That(xmlModel.Plugins).IsEquivalentTo(["admin", "metrics"]);
        await Assert.That(yamlModel.Enabled).IsFalse();
        await Assert.That(yamlModel.RetryCount).IsEqualTo(0);
        await Assert.That(yamlModel.Label).IsNull();
        await Assert.That(yamlModel.Database!.Port).IsEqualTo(6432);
        await Assert.That(yamlModel.Plugins).IsEquivalentTo(["admin", "metrics"]);
        await Assert
            .That(xml.ReadSchemaMetadata(in xmlSequence))
            .IsEqualTo(AppSettings.ConfiglueSchema.ToMetadata());
        await Assert
            .That(yaml.ReadSchemaMetadata(in yamlSequence))
            .IsEqualTo(AppSettings.ConfiglueSchema.ToMetadata());
    }
}
