using System.Buffers;
using System.Text;
using Configlue.Codecs;
using Configlue.Provider.Json;

namespace Configlue.Tests;

public sealed class JsonUtf8BomTests
{
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public void SchemaMetadataReaderRecognizesSegmentedUtf8Bom(int firstSegmentLength)
    {
        ReadOnlySequence<byte> source = Segmented(
            Encoding.UTF8.GetPreamble().Concat("{\"$configlue\":{\"version\":2},\"$value\":\"ok\"}"u8.ToArray()).ToArray(),
            firstSegmentLength
        );
        var codec = new JsonStateCodec<string>();
        var context = default(StateCodecContext);

        codec.ReadSchemaMetadata(in source).ShouldBe(new StateSchemaMetadata(null, 2));
        codec.Deserialize(in source, in context).ShouldBe("ok");
    }

    private static ReadOnlySequence<byte> Segmented(byte[] bytes, int firstSegmentLength)
    {
        var first = new TestSegment(bytes.AsMemory(0, firstSegmentLength));
        var last = first.Append(bytes.AsMemory(firstSegmentLength));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public TestSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new TestSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
