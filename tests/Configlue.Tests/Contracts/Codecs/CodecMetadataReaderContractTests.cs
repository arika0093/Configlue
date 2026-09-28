using System.Buffers;
using System.Text;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;

namespace Configlue.Tests;

public sealed class CodecMetadataReaderContractTests
{
    [Test]
    public void IStateSchemaMetadataReader_ExtractsAndOmitsMetadataAcrossFormats()
    {
        IStateSchemaMetadataReader json = new JsonStateCodec<string>();
        IStateSchemaMetadataReader xml = new XmlStateCodec<string>();
        IStateSchemaMetadataReader yaml = new YamlStateCodec<string>();
        var expected = new StateSchemaMetadata("contract-model", 7);
        var jsonPayload = Sequence(
            """{"$configlue":{"id":"contract-model","version":7},"$value":"value"}"""
        );
        var xmlPayload = Sequence(
            """<configlue id="contract-model" version="7"><value>value</value></configlue>"""
        );
        var yamlPayload = Sequence(
            """
            $configlue:
              id: contract-model
              version: 7
            $value: value
            """
        );
        var unmarkedJsonPayload = Sequence("\"value\"");
        var unmarkedXmlPayload = Sequence("<value>value</value>");
        var unmarkedYamlPayload = Sequence("Value: 1");

        json.ReadSchemaMetadata(in jsonPayload).ShouldBe(expected);
        xml.ReadSchemaMetadata(in xmlPayload).ShouldBe(expected);
        yaml.ReadSchemaMetadata(in yamlPayload).ShouldBe(expected);
        json.ReadSchemaMetadata(in unmarkedJsonPayload).ShouldBeNull();
        xml.ReadSchemaMetadata(in unmarkedXmlPayload).ShouldBeNull();
        yaml.ReadSchemaMetadata(in unmarkedYamlPayload).ShouldBeNull();
    }

    private static ReadOnlySequence<byte> Sequence(string content) =>
        new(Encoding.UTF8.GetBytes(content));
}
