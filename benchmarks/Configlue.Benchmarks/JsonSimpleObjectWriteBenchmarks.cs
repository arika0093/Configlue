using System.Buffers;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Codecs;
using Configlue.Provider.Json;
using Configlue.State;

[MemoryDiagnoser]
public class JsonSimpleObjectWriteBenchmarks
{
    private readonly ArrayBufferWriter<byte> _buffer = new();
    private readonly JsonStateCodec<JsonSimpleObjectWriteModel> _codec = new();
    private readonly JsonSimpleObjectWriteModel _value = new();
    private readonly StateCodecContext _context = new(new StateSchemaMetadata("plain-write", 1));

    [GlobalSetup]
    public void Setup() => Serialize();

    [Benchmark]
    public void Serialize()
    {
        _buffer.Clear();
        _codec.Serialize(_value, _buffer, in _context);
    }
}

public sealed class JsonSimpleObjectWriteModel
{
    [JsonPropertyOrder(2)]
    public int Counter { get; set; } = 42;

    [JsonPropertyOrder(-1)]
    public string Name { get; set; } = "benchmark";

    public bool Enabled { get; set; } = true;

    public string[] Tags { get; set; } = ["first", "second"];
}
