using System.Buffers;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Codecs;
using Configlue.Provider.Json;
using Configlue.State;

// Follow-up #214 for #166/#167 (JSON side; MessagePack side stays in
// MessagePackCodecAllocationBenchmarks). The class name carries the issue
// number so #210's end-to-end reader benchmarks can merge without renames:
// this class covers the codec-level matrix (schema/no-schema x
// Simple/Detailed) with the embedded-metadata read made explicit.
// Deserialize is the payload path; DeserializeWithMetadata is the single-pass
// embedded-metadata path from #167; ReadSchemaMetadata is the split step used
// to quantify what the single pass saves. All cases run over the generated
// fragment fast path (typed JsonStateCodec<T> over a generated Fragment).
[ConfiglueModel("bench-214-json-metadata", Version = 1)]
public partial class Issue214JsonMetadataSettings
{
    public int Counter { get; set; }

    public string Name { get; set; } = string.Empty;
}

[MemoryDiagnoser]
public class JsonMetadataBearingReadBenchmarks214
{
    private JsonStateCodec<Issue214JsonMetadataSettings.Fragment> _codec = null!;
    private StateCodecContext _context;
    private ReadOnlySequence<byte> _document;

    [Params(false, true)]
    public bool IncludeSchema { get; set; }

    [Params(DocumentLayout.Simple, DocumentLayout.Detailed)]
    public DocumentLayout Layout { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _codec = new JsonStateCodec<Issue214JsonMetadataSettings.Fragment>(
            documentLayout: new DocumentLayoutOptions { Layout = Layout }
        );
        var fragment = new Issue214JsonMetadataSettings.Fragment
        {
            Counter = Optional<int>.Present(42),
            Name = Optional<string>.Present("metadata benchmark"),
        };
        _context = IncludeSchema
            ? new StateCodecContext(new StateSchemaMetadata("bench-214-json-metadata", 1))
            : default;

        var buffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(fragment, buffer, in _context);
        _document = new ReadOnlySequence<byte>(buffer.WrittenMemory.ToArray());

        // Warm up each path, including the envelope/single-pass branches.
        _ = _codec.Deserialize(in _document, in _context);
        _ = _codec.DeserializeWithMetadata(in _document, in _context);
        _ = _codec.ReadSchemaMetadata(in _document);
    }

    [Benchmark]
    public Issue214JsonMetadataSettings.Fragment? Deserialize() =>
        _codec.Deserialize(in _document, in _context);

    [Benchmark]
    public StateCodecDecodeResult<Issue214JsonMetadataSettings.Fragment> DeserializeWithMetadata() =>
        _codec.DeserializeWithMetadata(in _document, in _context);

    [Benchmark]
    public StateSchemaMetadata? ReadSchemaMetadata() => _codec.ReadSchemaMetadata(in _document);
}
