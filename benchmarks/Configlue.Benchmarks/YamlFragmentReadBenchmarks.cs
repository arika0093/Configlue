using System.Buffers;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Codecs;
using Configlue.CompilerServices;
using Configlue.Provider.Yaml;

/// <summary>Separates reused YAML fragment decoding from cold codec construction.</summary>
[MemoryDiagnoser]
public class YamlFragmentReadBenchmarks
{
    [Params(4, 16, 64, 128)]
    public int MemberCount { get; set; }

    [Params(false, true)]
    public bool Sparse { get; set; }

    private ConfiglueModelSchema _schema = null!;
    private Type _fragmentType = null!;
    private YamlStateCodec _codec = null!;
    private ReadOnlySequence<byte> _payload;
    private StateCodecContext _context;

    [GlobalSetup]
    public void Setup()
    {
        IConfiglueFragment fragment = MemberCount switch
        {
            4 => MemberLookup4Settings.Fragment.Empty,
            16 => MemberLookup16Settings.Fragment.Empty,
            64 => MemberLookup64Settings.Fragment.Empty,
            _ => MemberLookup128Settings.Fragment.Empty,
        };
        _schema = fragment.Schema;
        _fragmentType = fragment.GetType();
        var presentCount = Sparse ? 1 : MemberCount;
        for (var id = 0; id < presentCount; id++)
        {
            fragment = fragment.WithMember(id, id + 7);
        }

        _context = new StateCodecContext(_schema.ToMetadata());
        _codec = new YamlStateCodec(modelSchema: _schema);
        var writer = new ArrayBufferWriter<byte>();
        _codec.Serialize(_fragmentType, fragment, writer, in _context);
        _payload = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Verify(ReusedCodec(), presentCount);
        Verify(NewCodec(), presentCount);
        // The default Simple layout emits a version, without a model ID.
        var metadata = _codec.ReadSchemaMetadata(in _payload);
        if (metadata?.Version != _schema.Version || metadata?.ModelId is not null)
        {
            throw new InvalidOperationException("YAML fragment fixture lost its schema metadata.");
        }
    }

    private static void Verify(IConfiglueFragment? fragment, int presentCount)
    {
        if (fragment is null)
        {
            throw new InvalidOperationException("YAML fragment fixture decoded to null.");
        }

        var members = fragment.EnumeratePresentMembers().ToArray();
        if (
            members.Length != presentCount
            || members.Any(member => (int)member.Value! != member.Id + 7)
        )
        {
            throw new InvalidOperationException(
                "YAML fragment fixture changed values or presence."
            );
        }
    }

    [Benchmark]
    public IConfiglueFragment? ReusedCodec() =>
        (IConfiglueFragment?)_codec.Deserialize(_fragmentType, in _payload, in _context);

    [Benchmark]
    public IConfiglueFragment? NewCodec() =>
        (IConfiglueFragment?)
            new YamlStateCodec(modelSchema: _schema).Deserialize(
                _fragmentType,
                in _payload,
                in _context
            );
}
