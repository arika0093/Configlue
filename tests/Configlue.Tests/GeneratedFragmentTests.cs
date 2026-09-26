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

        (fragment.Enabled.IsPresent).ShouldBeTrue();
        (fragment.Enabled.Value).ShouldBeFalse();
        (fragment.RetryCount.IsPresent).ShouldBeFalse();
        (fragment.Label.IsPresent).ShouldBeTrue();
        (fragment.Label.Value).ShouldBeNull();
        (value.Enabled).ShouldBeFalse();
        (value.RetryCount).ShouldBe(3);
        (value.Database!.Host).ShouldBe("localhost");
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

        (merged.Enabled).ShouldBeFalse();
        (merged.Database!.Host).ShouldBe("db.local");
        (merged.Database.Port).ShouldBe(6432);
        ((merged.Plugins))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "base", "custom" }).OrderBy(static item => item));
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

        (diff.Enabled.Value).ShouldBeFalse();
        (diff.RetryCount.IsPresent).ShouldBeFalse();
        (diff.Label.IsPresent).ShouldBeTrue();
        (diff.Label.Value).ShouldBeNull();
        (patched.Enabled.Value).ShouldBeFalse();
        (patched.RetryCount.IsPresent).ShouldBeFalse();
        (
            new AppSettings.Fragment { Enabled = Optional<bool>.Present(false) }.IsEmpty
        ).ShouldBeFalse();
        (AppSettings.ConfiglueSchema.Version).ShouldBe(2);
        (AppSettings.ConfiglueSchema.Id).ShouldBe("app-settings");
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

        (encoded).ShouldContain("\"$configlue\"");
        (encoded).ShouldContain("\"Label\":null");
        (encoded).ShouldNotContain("RetryCount");
        (decodedFragment.Enabled.IsPresent).ShouldBeTrue();
        (decodedFragment.Enabled.Value).ShouldBeFalse();
        (decodedFragment.Label.IsPresent).ShouldBeTrue();
        (decodedFragment.Label.Value).ShouldBeNull();
        (decodedFragment.RetryCount.IsPresent).ShouldBeFalse();
        (schema).ShouldBe(new StateSchemaMetadata("app-settings", 2));
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

        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
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

        (stored.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
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

        (decoded.Enabled.IsPresent).ShouldBeTrue();
        (decoded.Enabled.Value).ShouldBeFalse();
        (decoded.RetryCount.IsPresent).ShouldBeFalse();
        (decoded.Label.IsPresent).ShouldBeTrue();
        (decoded.Label.Value).ShouldBeNull();
        (decoded.Database.IsPresent).ShouldBeTrue();
        (decoded.Database.Value!.Host.Value).ShouldBe("db.local");
        (decoded.Database.Value!.Port.IsPresent).ShouldBeFalse();
        ((decoded.Plugins.Value!))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "admin" }).OrderBy(static item => item));
        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(
            new StateSchemaMetadata("app-settings", 2)
        );
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

        (decoded.Enabled.IsPresent).ShouldBeTrue();
        (decoded.Enabled.Value).ShouldBeFalse();
        (decoded.RetryCount.IsPresent).ShouldBeFalse();
        (decoded.Label.IsPresent).ShouldBeTrue();
        (decoded.Label.Value).ShouldBeNull();
        (decoded.Database.IsPresent).ShouldBeTrue();
        (decoded.Database.Value!.Host.Value).ShouldBe("db.local");
        (decoded.Database.Value!.Port.IsPresent).ShouldBeFalse();
        ((decoded.Plugins.Value!))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "admin" }).OrderBy(static item => item));
        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(
            new StateSchemaMetadata("app-settings", 2)
        );
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

        (xmlModel.Enabled).ShouldBeFalse();
        (xmlModel.RetryCount).ShouldBe(0);
        (xmlModel.Label).ShouldBeNull();
        (xmlModel.Database!.Port).ShouldBe(6432);
        ((xmlModel.Plugins))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "admin", "metrics" }).OrderBy(static item => item));
        (yamlModel.Enabled).ShouldBeFalse();
        (yamlModel.RetryCount).ShouldBe(0);
        (yamlModel.Label).ShouldBeNull();
        (yamlModel.Database!.Port).ShouldBe(6432);
        ((yamlModel.Plugins))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "admin", "metrics" }).OrderBy(static item => item));
        (xml.ReadSchemaMetadata(in xmlSequence)).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
        (yaml.ReadSchemaMetadata(in yamlSequence)).ShouldBe(
            AppSettings.ConfiglueSchema.ToMetadata()
        );
    }
}
