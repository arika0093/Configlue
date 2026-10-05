using System.Buffers;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Codecs;
using Configlue.Provider.Xml;
using Configlue.State;

[ConfiglueModel("bench.xml.buffers", Version = 1)]
public partial class XmlCodecBufferSettings
{
    public int Counter { get; set; }
    public string Text { get; set; } = string.Empty;
}

public enum XmlCodecInputShape
{
    ArraySlice,
    Segmented,
    MemoryManager,
}

/// <summary>Measures XML decoding and metadata parsing across buffer ownership shapes.</summary>
[MemoryDiagnoser]
public class XmlCodecReadBenchmarks
{
    [Params(0, 4096, 65536)]
    public int TextLength { get; set; }

    [Params(
        XmlCodecInputShape.ArraySlice,
        XmlCodecInputShape.Segmented,
        XmlCodecInputShape.MemoryManager
    )]
    public XmlCodecInputShape InputShape { get; set; }

    private readonly XmlStateCodec<XmlCodecBufferSettings.Fragment> _codec = new();
    private ReadOnlySequence<byte> _payload;
    private StateCodecContext _context;
    private IDisposable? _owner;

    [GlobalSetup]
    public void Setup()
    {
        var fragment = XmlCodecBufferFixture.CreateFragment(TextLength);
        _context = new StateCodecContext(fragment.Schema.ToMetadata());
        var writer = new ArrayBufferWriter<byte>();
        _codec.Serialize(fragment, writer, in _context);
        var surrounded = new byte[writer.WrittenCount + 24];
        Array.Fill(surrounded, (byte)0xff);
        writer.WrittenSpan.CopyTo(surrounded.AsSpan(17));
        var slice = surrounded.AsMemory(17, writer.WrittenCount);
        switch (InputShape)
        {
            case XmlCodecInputShape.ArraySlice:
                _payload = new ReadOnlySequence<byte>(slice);
                break;
            case XmlCodecInputShape.Segmented:
                var first = new Segment(slice[..1]);
                var middle = first.Append(slice[1..(slice.Length / 2)]);
                var last = middle.Append(slice[(slice.Length / 2)..]);
                _payload = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
                break;
            default:
                var owner = new ManagedMemoryOwner(slice.ToArray());
                _owner = owner;
                _payload = new ReadOnlySequence<byte>(owner.Memory);
                break;
        }

        if (!_payload.ToArray().AsSpan().SequenceEqual(writer.WrittenSpan))
        {
            throw new InvalidOperationException(
                "XML input fixture includes bytes outside its slice."
            );
        }

        XmlCodecBufferFixture.Verify(_codec.Deserialize(in _payload, in _context), TextLength);
        var untyped = (XmlCodecBufferSettings.Fragment?)
            new XmlStateCodec().Deserialize(
                typeof(XmlCodecBufferSettings.Fragment),
                in _payload,
                in _context
            );
        XmlCodecBufferFixture.Verify(untyped, TextLength);
        if (_codec.ReadSchemaMetadata(in _payload) != _context.Schema)
        {
            throw new InvalidOperationException("XML input fixture lost its schema metadata.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _owner?.Dispose();

    [Benchmark]
    public XmlCodecBufferSettings.Fragment? Deserialize() =>
        _codec.Deserialize(in _payload, in _context);

    [Benchmark]
    public StateSchemaMetadata? ReadMetadata() => _codec.ReadSchemaMetadata(in _payload);

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private sealed class ManagedMemoryOwner : MemoryManager<byte>
    {
        private readonly byte[] _bytes;

        public ManagedMemoryOwner(byte[] bytes) => _bytes = bytes;

        public override Span<byte> GetSpan() => _bytes;

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}

/// <summary>Measures XML writes to a reusable destination without storage I/O.</summary>
[MemoryDiagnoser]
public class XmlCodecWriteBenchmarks
{
    [Params(0, 4096, 65536)]
    public int TextLength { get; set; }

    private readonly XmlStateCodec<XmlCodecBufferSettings.Fragment> _codec = new();
    private readonly ArrayBufferWriter<byte> _writer = new();
    private XmlCodecBufferSettings.Fragment _fragment = null!;
    private StateCodecContext _context;

    [GlobalSetup]
    public void Setup()
    {
        _fragment = XmlCodecBufferFixture.CreateFragment(TextLength);
        _context = new StateCodecContext(_fragment.Schema.ToMetadata());
        Serialize();
        var payload = new ReadOnlySequence<byte>(_writer.WrittenMemory);
        XmlCodecBufferFixture.Verify(_codec.Deserialize(in payload, in _context), TextLength);
    }

    [Benchmark]
    public int Serialize()
    {
        _writer.Clear();
        _codec.Serialize(_fragment, _writer, in _context);
        return _writer.WrittenCount;
    }
}

internal static class XmlCodecBufferFixture
{
    internal static XmlCodecBufferSettings.Fragment CreateFragment(int textLength) =>
        new()
        {
            Counter = Optional<int>.Present(7),
            Text = Optional<string>.Present(new string('界', textLength)),
        };

    internal static void Verify(XmlCodecBufferSettings.Fragment? fragment, int textLength)
    {
        if (
            fragment is null
            || fragment.Counter.Value != 7
            || fragment.Text.Value != new string('界', textLength)
        )
        {
            throw new InvalidOperationException(
                "XML buffer fixture did not round-trip its sparse values."
            );
        }
    }
}
