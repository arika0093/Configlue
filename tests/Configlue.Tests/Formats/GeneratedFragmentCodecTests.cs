using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Xml.Linq;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;

namespace Configlue.Tests;

// Codec binding for generated fragments. The models under test live in
// GeneratedFragmentTests.cs; fragment presence/merge/diff/patch semantics and
// historical migration stay there.
public sealed class GeneratedFragmentCodecTests
{
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

        (encoded).ShouldContain("\"$version\":2");
        (encoded).ShouldNotContain("$configlue");
        (encoded).ShouldContain("\"Label\":null");
        (encoded).ShouldNotContain("RetryCount");
        (decodedFragment.Enabled.IsPresent).ShouldBeTrue();
        (decodedFragment.Enabled.Value).ShouldBeFalse();
        (decodedFragment.Label.IsPresent).ShouldBeTrue();
        (decodedFragment.Label.Value).ShouldBeNull();
        (decodedFragment.RetryCount.IsPresent).ShouldBeFalse();
        (schema).ShouldBe(new StateSchemaMetadata(null, 2));

        var detailed = new JsonStateCodec<AppSettings.Fragment>(
            documentLayout: new DocumentLayoutOptions { Layout = DocumentLayout.Detailed }
        );
        var detailedBuffer = new System.Buffers.ArrayBufferWriter<byte>();
        detailed.Serialize(fragment, detailedBuffer, in context);
        var detailedEncoded = Encoding.UTF8.GetString(detailedBuffer.WrittenSpan);
        var detailedSequence = new ReadOnlySequence<byte>(detailedBuffer.WrittenMemory);

        (detailedEncoded).ShouldContain("\"$configlue\"");
        (detailedEncoded).ShouldContain("\"$value\"");
        (detailed.ReadSchemaMetadata(in detailedSequence)).ShouldBe(
            new StateSchemaMetadata("app-settings", 2)
        );
        // Reads accept both layouts regardless of the configured write layout.
        (detailed.Deserialize(in sequence, default)!.Enabled.Value).ShouldBeFalse();
        (codec.Deserialize(in detailedSequence, default)!.Enabled.Value).ShouldBeFalse();
    }

    [Test]
    public async Task JsonCodec_DynamicGeneratedFragmentWritesSimpleSchemaDirectly()
    {
        var codec = new JsonStateCodec();
        var context = new StateCodecContext(new StateSchemaMetadata("app-settings", 2));
        var fragment = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Label = Optional<string?>.Present("direct"),
        };
        var buffer = new ArrayBufferWriter<byte>();

        codec.Serialize(typeof(AppSettings.Fragment), fragment, buffer, in context);

        var encoded = Encoding.UTF8.GetString(buffer.WrittenSpan);
        encoded.ShouldContain("\"$version\":2");
        encoded.ShouldContain("\"Enabled\":false");
        encoded.ShouldContain("\"Label\":\"direct\"");
        encoded.ShouldNotContain("\"$value\"");
        encoded.ShouldNotContain("RetryCount");
    }

    [Test]
    public async Task JsonCodec_DeserializesProjectedPayloadAfterWriterBufferGrows()
    {
        var label = new string('x', 4096);
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var fragment = new AppSettings.Fragment { Label = Optional<string?>.Present(label) };
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var context = new StateCodecContext(new StateSchemaMetadata("app-settings", 2));

        codec.Serialize(fragment, buffer, in context);
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        var decoded = codec.Deserialize(in sequence, default);

        (decoded!.Label.Value).ShouldBe(label);
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

        // The simple layout stores the version without a model ID.
        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(new StateSchemaMetadata(null, 2));
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
    public void XmlCodec_RoundTripsSupportedCollectionShapesAsAssignableValues()
    {
        var model = new XmlCollectionShapesSettings
        {
            ArrayValues = [1, 2],
            ListValues = [3, 4],
            ReadOnlyListValues = [5, 6],
            HashSetValues = [7, 8],
            SetValues = new HashSet<int> { 9, 10 },
            ReadOnlySetValues = new TestReadOnlySet<int>([11, 12]),
            DictionaryValues = new Dictionary<string, int> { ["one"] = 1 },
            ReadOnlyDictionaryValues = new Dictionary<string, int> { ["two"] = 2 },
            SortedDictionaryValues = new SortedDictionary<string, int> { ["three"] = 3 },
        };
        var codec = new XmlStateCodec<XmlCollectionShapesSettings>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.Serialize(model, buffer, default);
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        var decoded = codec.Deserialize(in sequence, default)!;

        decoded.ArrayValues.ShouldBe([1, 2]);
        decoded.ListValues.ShouldBe([3, 4]);
        decoded.ReadOnlyListValues.ShouldBe([5, 6]);
        decoded.HashSetValues.ShouldBe(new HashSet<int> { 7, 8 });
        decoded.SetValues.ShouldBe(new HashSet<int> { 9, 10 });
        decoded.ReadOnlySetValues.OrderBy(static item => item).ShouldBe([11, 12]);
        decoded.DictionaryValues.ShouldBe(new Dictionary<string, int> { ["one"] = 1 });
        decoded.ReadOnlyDictionaryValues.ShouldBe(new Dictionary<string, int> { ["two"] = 2 });
        decoded.SortedDictionaryValues.ShouldBe(
            new SortedDictionary<string, int> { ["three"] = 3 }
        );
    }

    [Test]
    public void XmlCodec_RejectsExoticCollectionShapes()
    {
        // Exotic shapes (issue #280) have no XML materialization contract:
        // serializing succeeds (sequences are enumerable) but deserializing the
        // fragment member fails with XmlException.
        var model = new XmlExoticShapesSettings
        {
            QueueValues = new Queue<int>([1, 2]),
            LinkedListValues = new LinkedList<int>([3, 4]),
        };
        var codec = new XmlStateCodec<XmlExoticShapesSettings>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.Serialize(model, buffer, default);
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);

        Should.Throw<System.Xml.XmlException>(() => codec.Deserialize(in sequence, default));
    }

    [Test]
    public void XmlCodec_RoundTripsNullDictionaryValuesViaXsiNil()
    {
        var model = new XmlNullDictionarySettings
        {
            Values = new()
            {
                ["null-list"] = null,
                ["empty-list"] = [],
                ["values"] = [1, 2],
            },
            NullableInts = new() { ["null-int"] = null, ["value"] = 42 },
            NullableStrings = new() { ["null-string"] = null, ["value"] = "hello" },
            Nested = new()
            {
                ["null-dict"] = null,
                ["empty-dict"] = new(),
                ["value"] = new() { ["k"] = 1 },
            },
        };

        // Typed codec.
        var typedCodec = new XmlStateCodec<XmlNullDictionarySettings>();
        var buffer = new ArrayBufferWriter<byte>();
        typedCodec.Serialize(model, buffer, default);
        var payload = buffer.WrittenMemory.ToArray();
        var xml = Encoding.UTF8.GetString(payload);
        (xml.Contains("nil")).ShouldBeTrue();
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        var decoded = typedCodec.Deserialize(in sequence, default)!;
        AssertNullDictionaryRoundTrip(decoded);

        // Dynamic codec.
        var dynamicCodec = new XmlStateCodec();
        var dynamicBuffer = new ArrayBufferWriter<byte>();
        dynamicCodec.Serialize(typeof(XmlNullDictionarySettings), model, dynamicBuffer, default);
        var dynamicSequence = new ReadOnlySequence<byte>(dynamicBuffer.WrittenMemory);
        var dynamicDecoded = (XmlNullDictionarySettings)
            dynamicCodec.Deserialize(
                typeof(XmlNullDictionarySettings),
                in dynamicSequence,
                default
            )!;
        AssertNullDictionaryRoundTrip(dynamicDecoded);

        // Generated fragment codec.
        var fragmentCodec = new XmlStateCodec<XmlNullDictionarySettings.Fragment>();
        var fragmentBuffer = new ArrayBufferWriter<byte>();
        var fragment = XmlNullDictionarySettings.Fragment.From(model);
        fragmentCodec.Serialize(fragment, fragmentBuffer, default);
        var fragmentSequence = new ReadOnlySequence<byte>(fragmentBuffer.WrittenMemory);
        var decodedFragment = fragmentCodec.Deserialize(in fragmentSequence, default)!;
        AssertNullDictionaryRoundTrip(decodedFragment.ToModel());
    }

    [Test]
    public void XmlCodec_RejectsNilDictionaryValueForNonNullableValueType()
    {
        var codec = new XmlStateCodec<XmlNonNullDictionarySettings>();
        var valid = new XmlNonNullDictionarySettings { Values = new() { ["ok"] = 1 } };
        var buffer = new ArrayBufferWriter<byte>();
        codec.Serialize(valid, buffer, default);
        var document = System.Xml.Linq.XDocument.Parse(
            Encoding.UTF8.GetString(buffer.WrittenMemory.ToArray())
        );
        var valueWrapper = document
            .Descendants("member")
            .First(element => (string?)element.Attribute("name") == "Values")
            .Descendants("item")
            .Elements("value")
            .First();
        valueWrapper.RemoveNodes();
        valueWrapper.SetAttributeValue(
            System.Xml.Linq.XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"),
            "true"
        );
        var tampered = Encoding.UTF8.GetBytes(
            (document.Declaration?.ToString() ?? string.Empty) + document.ToString()
        );
        var tamperedSequence = new ReadOnlySequence<byte>(tampered);

        Should.Throw<System.Xml.XmlException>(() =>
            codec.Deserialize(in tamperedSequence, default)
        );
    }

    private static void AssertNullDictionaryRoundTrip(XmlNullDictionarySettings decoded)
    {
        (decoded.Values.ContainsKey("null-list")).ShouldBeTrue();
        (decoded.Values["null-list"]).ShouldBeNull();
        (decoded.Values["empty-list"]).ShouldBe(new List<int>());
        (decoded.Values["empty-list"] is null).ShouldBeFalse();
        (decoded.Values["values"]).ShouldBe(new List<int> { 1, 2 });
        (decoded.NullableInts["null-int"]).ShouldBeNull();
        (decoded.NullableInts["value"]).ShouldBe(42);
        (decoded.NullableStrings["null-string"]).ShouldBeNull();
        (decoded.NullableStrings["value"]).ShouldBe("hello");
        (decoded.Nested.ContainsKey("null-dict")).ShouldBeTrue();
        (decoded.Nested["null-dict"]).ShouldBeNull();
        (decoded.Nested["empty-dict"]).ShouldBe(new Dictionary<string, int>());
        (decoded.Nested["empty-dict"] is null).ShouldBeFalse();
        (decoded.Nested["value"]).ShouldBe(new Dictionary<string, int> { ["k"] = 1 });
    }

    [Test]
    public async Task YamlCodec_RoundTripsSparseNestedValuesAndSchemaMetadata()
    {
        var codec = new YamlStateCodec<AppSettings.Fragment>(
            modelSchema: AppSettings.FragmentSchema,
            serializerOptions: AppSettingsYamlContext.Default.Options
        );
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
        (codec.ReadSchemaMetadata(in sequence)).ShouldBe(new StateSchemaMetadata(null, 2));
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
        // The simple YAML layout stores the version without a model ID.
        (yaml.ReadSchemaMetadata(in yamlSequence)).ShouldBe(new StateSchemaMetadata(null, 2));
    }
}
