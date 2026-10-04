using System.Buffers;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.CompilerServices;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;

[ConfiglueModel("bench.member-lookup-4", Version = 1)]
public partial class MemberLookup4Settings
{
    public int M00 { get; set; }
    public int M01 { get; set; }
    public int M02 { get; set; }
    public int M03 { get; set; }
}

[ConfiglueModel("bench.member-lookup-16", Version = 1)]
public partial class MemberLookup16Settings
{
    public int M00 { get; set; }
    public int M01 { get; set; }
    public int M02 { get; set; }
    public int M03 { get; set; }
    public int M04 { get; set; }
    public int M05 { get; set; }
    public int M06 { get; set; }
    public int M07 { get; set; }
    public int M08 { get; set; }
    public int M09 { get; set; }
    public int M10 { get; set; }
    public int M11 { get; set; }
    public int M12 { get; set; }
    public int M13 { get; set; }
    public int M14 { get; set; }
    public int M15 { get; set; }
}

[ConfiglueModel("bench.member-lookup-64", Version = 1)]
public partial class MemberLookup64Settings
{
    public int M00 { get; set; }
    public int M01 { get; set; }
    public int M02 { get; set; }
    public int M03 { get; set; }
    public int M04 { get; set; }
    public int M05 { get; set; }
    public int M06 { get; set; }
    public int M07 { get; set; }
    public int M08 { get; set; }
    public int M09 { get; set; }
    public int M10 { get; set; }
    public int M11 { get; set; }
    public int M12 { get; set; }
    public int M13 { get; set; }
    public int M14 { get; set; }
    public int M15 { get; set; }
    public int M16 { get; set; }
    public int M17 { get; set; }
    public int M18 { get; set; }
    public int M19 { get; set; }
    public int M20 { get; set; }
    public int M21 { get; set; }
    public int M22 { get; set; }
    public int M23 { get; set; }
    public int M24 { get; set; }
    public int M25 { get; set; }
    public int M26 { get; set; }
    public int M27 { get; set; }
    public int M28 { get; set; }
    public int M29 { get; set; }
    public int M30 { get; set; }
    public int M31 { get; set; }
    public int M32 { get; set; }
    public int M33 { get; set; }
    public int M34 { get; set; }
    public int M35 { get; set; }
    public int M36 { get; set; }
    public int M37 { get; set; }
    public int M38 { get; set; }
    public int M39 { get; set; }
    public int M40 { get; set; }
    public int M41 { get; set; }
    public int M42 { get; set; }
    public int M43 { get; set; }
    public int M44 { get; set; }
    public int M45 { get; set; }
    public int M46 { get; set; }
    public int M47 { get; set; }
    public int M48 { get; set; }
    public int M49 { get; set; }
    public int M50 { get; set; }
    public int M51 { get; set; }
    public int M52 { get; set; }
    public int M53 { get; set; }
    public int M54 { get; set; }
    public int M55 { get; set; }
    public int M56 { get; set; }
    public int M57 { get; set; }
    public int M58 { get; set; }
    public int M59 { get; set; }
    public int M60 { get; set; }
    public int M61 { get; set; }
    public int M62 { get; set; }
    public int M63 { get; set; }
}

[ConfiglueModel("bench.member-lookup-128", Version = 1)]
public partial class MemberLookup128Settings
{
    public int M000 { get; set; }
    public int M001 { get; set; }
    public int M002 { get; set; }
    public int M003 { get; set; }
    public int M004 { get; set; }
    public int M005 { get; set; }
    public int M006 { get; set; }
    public int M007 { get; set; }
    public int M008 { get; set; }
    public int M009 { get; set; }
    public int M010 { get; set; }
    public int M011 { get; set; }
    public int M012 { get; set; }
    public int M013 { get; set; }
    public int M014 { get; set; }
    public int M015 { get; set; }
    public int M016 { get; set; }
    public int M017 { get; set; }
    public int M018 { get; set; }
    public int M019 { get; set; }
    public int M020 { get; set; }
    public int M021 { get; set; }
    public int M022 { get; set; }
    public int M023 { get; set; }
    public int M024 { get; set; }
    public int M025 { get; set; }
    public int M026 { get; set; }
    public int M027 { get; set; }
    public int M028 { get; set; }
    public int M029 { get; set; }
    public int M030 { get; set; }
    public int M031 { get; set; }
    public int M032 { get; set; }
    public int M033 { get; set; }
    public int M034 { get; set; }
    public int M035 { get; set; }
    public int M036 { get; set; }
    public int M037 { get; set; }
    public int M038 { get; set; }
    public int M039 { get; set; }
    public int M040 { get; set; }
    public int M041 { get; set; }
    public int M042 { get; set; }
    public int M043 { get; set; }
    public int M044 { get; set; }
    public int M045 { get; set; }
    public int M046 { get; set; }
    public int M047 { get; set; }
    public int M048 { get; set; }
    public int M049 { get; set; }
    public int M050 { get; set; }
    public int M051 { get; set; }
    public int M052 { get; set; }
    public int M053 { get; set; }
    public int M054 { get; set; }
    public int M055 { get; set; }
    public int M056 { get; set; }
    public int M057 { get; set; }
    public int M058 { get; set; }
    public int M059 { get; set; }
    public int M060 { get; set; }
    public int M061 { get; set; }
    public int M062 { get; set; }
    public int M063 { get; set; }
    public int M064 { get; set; }
    public int M065 { get; set; }
    public int M066 { get; set; }
    public int M067 { get; set; }
    public int M068 { get; set; }
    public int M069 { get; set; }
    public int M070 { get; set; }
    public int M071 { get; set; }
    public int M072 { get; set; }
    public int M073 { get; set; }
    public int M074 { get; set; }
    public int M075 { get; set; }
    public int M076 { get; set; }
    public int M077 { get; set; }
    public int M078 { get; set; }
    public int M079 { get; set; }
    public int M080 { get; set; }
    public int M081 { get; set; }
    public int M082 { get; set; }
    public int M083 { get; set; }
    public int M084 { get; set; }
    public int M085 { get; set; }
    public int M086 { get; set; }
    public int M087 { get; set; }
    public int M088 { get; set; }
    public int M089 { get; set; }
    public int M090 { get; set; }
    public int M091 { get; set; }
    public int M092 { get; set; }
    public int M093 { get; set; }
    public int M094 { get; set; }
    public int M095 { get; set; }
    public int M096 { get; set; }
    public int M097 { get; set; }
    public int M098 { get; set; }
    public int M099 { get; set; }
    public int M100 { get; set; }
    public int M101 { get; set; }
    public int M102 { get; set; }
    public int M103 { get; set; }
    public int M104 { get; set; }
    public int M105 { get; set; }
    public int M106 { get; set; }
    public int M107 { get; set; }
    public int M108 { get; set; }
    public int M109 { get; set; }
    public int M110 { get; set; }
    public int M111 { get; set; }
    public int M112 { get; set; }
    public int M113 { get; set; }
    public int M114 { get; set; }
    public int M115 { get; set; }
    public int M116 { get; set; }
    public int M117 { get; set; }
    public int M118 { get; set; }
    public int M119 { get; set; }
    public int M120 { get; set; }
    public int M121 { get; set; }
    public int M122 { get; set; }
    public int M123 { get; set; }
    public int M124 { get; set; }
    public int M125 { get; set; }
    public int M126 { get; set; }
    public int M127 { get; set; }
}

/// <summary>
/// Generated member-ID lookup and codec benchmarks for issue #222.
/// </summary>
/// <remarks>
/// <see cref="MemberCount"/> selects a 4/16/64/128-member generated model. Every benchmark
/// runs a production hot path: <see cref="LookupAllMembers"/> resolves each generated ID
/// through the centralized <see cref="ConfiglueModelSchema.TryGetMember"/>,
/// <see cref="ResolveLeafPaths"/> resolves one-segment <see cref="ConfiglueMemberPath"/>
/// leaves, and the full-fragment benchmarks serialize every present member through the
/// XML/YAML codecs. A return to per-member linear scans shows up as quadratic growth in
/// <c>Mean</c> across <see cref="MemberCount"/> instead of linear growth.
/// </remarks>
[MemoryDiagnoser]
public class MemberLookupBenchmarks
{
    private ConfiglueModelSchema _schema = null!;
    private IConfiglueFragment _fullFragment = null!;
    private readonly XmlStateCodec _xml = new();
    private readonly YamlStateCodec _yaml = new();
    private readonly ArrayBufferWriter<byte> _writer = new();

    [Params(4, 16, 64, 128)]
    public int MemberCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        IConfiglueFragment empty = MemberCount switch
        {
            4 => MemberLookup4Settings.Fragment.Empty,
            16 => MemberLookup16Settings.Fragment.Empty,
            64 => MemberLookup64Settings.Fragment.Empty,
            _ => MemberLookup128Settings.Fragment.Empty,
        };
        _schema = empty.Schema;
        var fragment = empty;
        for (var id = 0; id < _schema.Members.Count; id++)
        {
            fragment = fragment.WithMember(id, id);
        }

        _fullFragment = fragment;
    }

    [Benchmark(Description = "Centralized TryGetMember over all member IDs")]
    public int LookupAllMembers()
    {
        var sum = 0;
        for (var id = 0; id < _schema.Members.Count; id++)
        {
            if (_schema.TryGetMember(id, out var member))
            {
                sum += member.Id;
            }
        }

        return sum;
    }

    [Benchmark(Description = "ConfiglueMemberPath leaf resolve over all member IDs")]
    public int ResolveLeafPaths()
    {
        var root = ConfiglueMemberPath.Root(_schema);
        var length = 0;
        for (var id = 0; id < _schema.Members.Count; id++)
        {
            length += root.Append(id).ResolveMember().Name.Length;
        }

        return length;
    }

    [Benchmark(Description = "XML serialize full sparse fragment")]
    public int XmlSerializeFull()
    {
        _writer.Clear();
        _xml.Serialize(_fullFragment.GetType(), _fullFragment, _writer, default);
        return _writer.WrittenCount;
    }

    [Benchmark(Description = "YAML serialize full sparse fragment")]
    public int YamlSerializeFull()
    {
        _writer.Clear();
        _yaml.Serialize(_fullFragment.GetType(), _fullFragment, _writer, default);
        return _writer.WrittenCount;
    }
}

/// <summary>
/// Sparse-fragment codec scaling for issue #222 on the 128-member model.
/// </summary>
/// <remarks>
/// <see cref="PresentCount"/> varies how many members are present while the schema stays
/// fixed. Per-member linear member-ID scans would make <c>Mean</c> grow with present
/// count even though each present member resolves in O(1) through the centralized lookup.
/// </remarks>
[MemoryDiagnoser]
public class SparseMemberLookupCodecBenchmarks
{
    private IConfiglueFragment _sparse = null!;
    private readonly XmlStateCodec _xml = new();
    private readonly YamlStateCodec _yaml = new();
    private readonly ArrayBufferWriter<byte> _writer = new();

    [Params(4, 16, 64, 128)]
    public int PresentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        IConfiglueFragment fragment = MemberLookup128Settings.Fragment.Empty;
        for (var id = 0; id < PresentCount; id++)
        {
            fragment = fragment.WithMember(id, id);
        }

        _sparse = fragment;
    }

    [Benchmark(Description = "XML serialize sparse fragment with growing present count")]
    public int XmlSerializeSparse()
    {
        _writer.Clear();
        _xml.Serialize(_sparse.GetType(), _sparse, _writer, default);
        return _writer.WrittenCount;
    }

    [Benchmark(Description = "YAML serialize sparse fragment with growing present count")]
    public int YamlSerializeSparse()
    {
        _writer.Clear();
        _yaml.Serialize(_sparse.GetType(), _sparse, _writer, default);
        return _writer.WrittenCount;
    }
}
