using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Codecs;
using Configlue.CompilerServices;
using Configlue.Provider.Yaml;

/// <summary>Measures a reused codec whose naming policy changes before every read.</summary>
[MemoryDiagnoser]
public class YamlMutableNamingPolicyBenchmarks
{
    private readonly PrefixPolicy _policy = new();
    private readonly ReadOnlySequence<byte>[] _payloads = new ReadOnlySequence<byte>[2];
    private YamlStateCodec _codec = null!;
    private Type _fragmentType = null!;
    private StateCodecContext _context;
    private int _index;

    [GlobalSetup]
    public void Setup()
    {
        IConfiglueFragment fragment = MemberLookup4Settings.Fragment.Empty;
        fragment = fragment.WithMember(0, 7);
        _fragmentType = fragment.GetType();
        _context = new StateCodecContext(fragment.Schema.ToMetadata());
        _codec = new YamlStateCodec(namingPolicy: _policy, modelSchema: fragment.Schema);
        for (var index = 0; index < 2; index++)
        {
            _policy.Prefix = Prefix(index);
            var writer = new ArrayBufferWriter<byte>();
            _codec.Serialize(_fragmentType, fragment, writer, in _context);
            _payloads[index] = new ReadOnlySequence<byte>(writer.WrittenMemory);
        }

        Verify(ReadChangedPolicy());
        Verify(ReadChangedPolicy());
    }

    private static void Verify(IConfiglueFragment? fragment)
    {
        var members = fragment?.EnumeratePresentMembers().ToArray();
        if (
            members is null
            || members.Length != 1
            || members[0].Id != 0
            || (int)members[0].Value! != 7
        )
        {
            throw new InvalidOperationException(
                "YAML naming-policy fixture cached a stale member name."
            );
        }
    }

    [Benchmark]
    public IConfiglueFragment? ReadChangedPolicy()
    {
        _index ^= 1;
        _policy.Prefix = Prefix(_index);
        return (IConfiglueFragment?)
            _codec.Deserialize(_fragmentType, in _payloads[_index], in _context);
    }

    private static string Prefix(int index) => index == 0 ? "first_" : "second_";

    private sealed class PrefixPolicy : JsonNamingPolicy
    {
        public string Prefix { get; set; } = string.Empty;

        public override string ConvertName(string name) => Prefix + name;
    }
}
