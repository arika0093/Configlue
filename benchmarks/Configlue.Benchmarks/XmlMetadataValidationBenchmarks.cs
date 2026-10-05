using System.Buffers;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BenchmarkDotNet.Attributes;
using Configlue.Provider.Xml;
using Configlue.State;

public enum XmlMetadataDocumentKind
{
    Valid,
    Unmarked,
    InvalidVersion,
    Empty,
    InvalidChild,
    TrailingData,
    UnmarkedTrailingData,
    InvalidVersionTrailingData,
    Dtd,
    UnknownEntity,
}

/// <summary>Measures XML metadata parsing for valid documents and rejected input.</summary>
[MemoryDiagnoser]
public class XmlMetadataValidationBenchmarks
{
    [ParamsAllValues]
    public XmlMetadataDocumentKind DocumentKind { get; set; }

    private readonly XmlStateCodec _codec = new();
    private ReadOnlySequence<byte> _payload;

    [GlobalSetup]
    public void Setup()
    {
        var text = new string('界', 4096);
        var valid =
            $"<configlue id=\"metadata-contract\" version=\"7\"><value>{text}</value></configlue>";
        var unmarked = $"<value>{text}</value>";
        var invalidVersion = $"<configlue version=\"0\"><value>{text}</value></configlue>";
        var document = DocumentKind switch
        {
            XmlMetadataDocumentKind.Valid => valid,
            XmlMetadataDocumentKind.Unmarked => unmarked,
            XmlMetadataDocumentKind.InvalidVersion => invalidVersion,
            XmlMetadataDocumentKind.Empty => string.Empty,
            XmlMetadataDocumentKind.InvalidChild =>
                $"<configlue version=\"7\"><value>{text}</wrong></configlue>",
            XmlMetadataDocumentKind.TrailingData => valid + "!",
            XmlMetadataDocumentKind.UnmarkedTrailingData => unmarked + "!",
            XmlMetadataDocumentKind.InvalidVersionTrailingData => invalidVersion + "!",
            XmlMetadataDocumentKind.UnknownEntity =>
                "<configlue version=\"7\"><value>&unknown;</value></configlue>",
            _ =>
                "<!DOCTYPE configlue [<!ENTITY content \"value\">]><configlue version=\"7\"><value>&content;</value></configlue>",
        };
        _payload = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(document));
        var expected = ReadReference();
        var actual = ReadMetadata();
        if (actual.Failed != expected.Failed || actual.Metadata != expected.Metadata)
        {
            throw new InvalidOperationException(
                "XML metadata fixture changed full-document validation."
            );
        }
    }

    [Benchmark]
    public (bool Failed, StateSchemaMetadata? Metadata) ReadMetadata()
    {
        try
        {
            return (false, _codec.ReadSchemaMetadata(in _payload));
        }
        catch (XmlException)
        {
            return (true, null);
        }
    }

    private (bool Failed, StateSchemaMetadata? Metadata) ReadReference()
    {
        try
        {
            using var memory = new MemoryStream(_payload.ToArray(), writable: false);
            using var reader = XmlReader.Create(
                memory,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }
            );
            var root = XDocument.Load(reader, LoadOptions.None).Root;
            if (
                root is null
                || root.Name.LocalName != "configlue"
                || !int.TryParse(
                    (string?)root.Attribute("version"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var version
                )
                || version < StateSchemaMetadata.InitialVersion
            )
            {
                return (false, null);
            }

            return (false, new StateSchemaMetadata((string?)root.Attribute("id"), version));
        }
        catch (XmlException)
        {
            return (true, null);
        }
    }
}
