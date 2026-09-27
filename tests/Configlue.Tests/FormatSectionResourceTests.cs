using System.Buffers;
using System.Text;
using System.Xml.Linq;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Configlue.Tests;

public sealed class FormatSectionResourceTests
{
    [Test]
    public async Task XmlSectionResource_UpdatesNestedGeneratedFragmentAndPreservesSiblings()
    {
        var codec = new XmlStateCodec<AppSettings.Fragment>();
        var fragmentBytes = Serialize(
            codec,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
        );
        var document = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "App",
                    new XElement(
                        "Settings",
                        XElement.Parse(Encoding.UTF8.GetString(fragmentBytes))
                    ),
                    new XElement("Other", new XElement("Value", "keep-nested"))
                ),
                new XElement("OtherSection", new XElement("Value", "keep-root"))
            )
        );
        var resource = new InMemoryResource();
        await resource.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))
            )
        );
        var section = new XmlSectionResource(resource, "App__Settings");
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "settings",
            section,
            codec
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        await options.ApplyPatchAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(9) }
        );

        var updated = XDocument.Parse(
            Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span)
        );
        var root = updated.Root!;
        var sectionValue =
            root.Element("App")?.Element("Settings")?.Element("configlue")
            ?? throw new InvalidOperationException(updated.ToString(SaveOptions.None));
        var retryCount = sectionValue
            .Elements("member")
            .Single(element => (string?)element.Attribute("name") == "RetryCount");
        ((string?)retryCount.Element("int")).ShouldBe("9");
        (root.Element("App")!.Element("Other")!.Element("Value")!.Value).ShouldBe("keep-nested");
        (root.Element("OtherSection")!.Element("Value")!.Value).ShouldBe("keep-root");
    }

    [Test]
    public async Task XmlSectionResource_CreatesMissingDocumentAndNestedPath()
    {
        var resource = new InMemoryResource();
        var section = new XmlSectionResource(resource, "App__Settings");
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "settings",
            section,
            new XmlStateCodec<AppSettings.Fragment>()
        );
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        await options.ApplyPatchAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
        );

        var document = XDocument.Parse(
            Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span)
        );
        var retryCount = document
            .Root!.Element("App")!
            .Element("Settings")!
            .Element("configlue")!
            .Elements("member")
            .Single(element => (string?)element.Attribute("name") == "RetryCount");
        ((string?)retryCount.Element("int")).ShouldBe("7");
    }

    [Test]
    public async Task YamlSectionResource_UpdatesNestedGeneratedFragmentAndPreservesSiblings()
    {
        var codec = new YamlStateCodec<AppSettings.Fragment>();
        var fragmentBytes = Serialize(
            codec,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
        );
        var sectionNode = LoadYaml(fragmentBytes);
        var document = new YamlMappingNode
        {
            {
                "App",
                new YamlMappingNode
                {
                    { "Settings", sectionNode },
                    {
                        "Other",
                        new YamlMappingNode { { "Value", "keep-nested" } }
                    },
                }
            },
            {
                "OtherSection",
                new YamlMappingNode { { "Value", "keep-root" } }
            },
        };
        var resource = new InMemoryResource();
        var yamlWithComments =
            "# A surrounding comment is accepted; comment preservation is not promised.\n"
            + Encoding.UTF8.GetString(SerializeYaml(document))
            + "\n# A trailing comment.\n";
        await resource.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes(yamlWithComments))
        );
        var section = new YamlSectionResource(resource, "App:Settings");
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "settings",
            section,
            codec
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        await options.ApplyPatchAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(9) }
        );

        var updatedRoot = LoadYaml((await resource.ReadAsync()).Content.Span);
        var app = GetMapping(updatedRoot, "App");
        var settings = GetMapping(app, "Settings");
        var values = GetMapping(settings, "$value");
        (((YamlScalarNode)GetNode(values, "RetryCount")).Value).ShouldBe("9");
        (((YamlScalarNode)GetNode(GetMapping(app, "Other"), "Value")).Value).ShouldBe(
            "keep-nested"
        );
        (
            ((YamlScalarNode)GetNode(GetMapping(updatedRoot, "OtherSection"), "Value")).Value
        ).ShouldBe("keep-root");
    }

    [Test]
    public async Task YamlSectionResource_CreatesMissingDocumentAndNestedPath()
    {
        var resource = new InMemoryResource();
        var section = new YamlSectionResource(resource, "App:Settings");
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "settings",
            section,
            new YamlStateCodec<AppSettings.Fragment>()
        );
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        await options.ApplyPatchAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
        );

        var updatedRoot = LoadYaml((await resource.ReadAsync()).Content.Span);
        var values = GetMapping(GetMapping(GetMapping(updatedRoot, "App"), "Settings"), "$value");
        (((YamlScalarNode)GetNode(values, "RetryCount")).Value).ShouldBe("7");
    }

    [Test]
    public async Task YamlSectionResource_DoesNotOverwriteMalformedDocument()
    {
        const string malformed = "App:\n  Settings: [unterminated\n";
        var resource = new InMemoryResource();
        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes(malformed)));
        var section = new YamlSectionResource(resource, "App:Settings");
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "settings",
            section,
            new YamlStateCodec<AppSettings.Fragment>()
        );
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        await Should.ThrowAsync<YamlException>(async () =>
            await options.ApplyPatchAsync(
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
            )
        );

        Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span).ShouldBe(malformed);
    }

    private static byte[] Serialize<T>(IStateCodec<T> codec, T value)
    {
        var output = new ArrayBufferWriter<byte>();
        var context = default(StateCodecContext);
        codec.Serialize(value, output, in context);
        return output.WrittenSpan.ToArray();
    }

    private static YamlNode LoadYaml(ReadOnlySpan<byte> content)
    {
        using var reader = new StringReader(Encoding.UTF8.GetString(content));
        var stream = new YamlStream();
        stream.Load(reader);
        return stream.Documents.Single().RootNode;
    }

    private static byte[] SerializeYaml(YamlNode node)
    {
        var stream = new YamlStream(new YamlDocument(node));
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        stream.Save(writer);
        return Encoding.UTF8.GetBytes(writer.ToString());
    }

    private static YamlMappingNode GetMapping(YamlNode node, string key) =>
        GetNode(node, key) as YamlMappingNode
        ?? throw new InvalidOperationException($"YAML node '{key}' is not a mapping.");

    private static YamlNode GetNode(YamlNode node, string key)
    {
        if (node is not YamlMappingNode mapping)
        {
            throw new InvalidOperationException("Expected a YAML mapping.");
        }

        return mapping
            .Children.First(pair => pair.Key is YamlScalarNode scalar && scalar.Value == key)
            .Value;
    }
}
