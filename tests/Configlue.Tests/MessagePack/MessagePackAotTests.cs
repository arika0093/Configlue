using System.Buffers;
using Configlue;
using Configlue.Provider.MessagePack;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace Configlue.Tests;

[ConfiglueModel("messagepack.aot")]
public partial class MessagePackAotSettings
{
    public int Count { get; set; }
    public string? Label { get; set; }
    public MessagePackNestedPoco Child { get; set; } = new();
    public List<int> Numbers { get; set; } = [];
}

public sealed class MessagePackAotTests
{
    [Test]
    public void MessagePackAot_UsesGeneratedFragmentsAndExplicitCollectionFormatters()
    {
        var options = MessagePackSerializerOptions.Standard.WithResolver(new AotResolver());
        var codec = new MessagePackStateCodec<MessagePackAotSettings.Fragment>(options);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Serialize(
            MessagePackAotSettings.Fragment.From(
                new MessagePackAotSettings
                {
                    Count = 42,
                    Label = null,
                    Child = new MessagePackNestedPoco { Enabled = true, Text = "aot" },
                    Numbers = [1, 3, 5],
                }
            ),
            buffer,
            default
        );
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        codec
            .ReadSchemaMetadata(sequence)
            .ShouldBe(new State.StateSchemaMetadata("messagepack.aot", 1));
        var read = codec.Deserialize(sequence, default)!;
        read.Count.Value.ShouldBe(42);
        read.Label.IsPresent.ShouldBeTrue();
        read.Label.Value.ShouldBeNull();
        read.Child.Value!.Text.Value.ShouldBe("aot");
        read.Numbers.Value!.SequenceEqual([1, 3, 5]).ShouldBeTrue();
    }

    private sealed class AotResolver : IFormatterResolver
    {
        private static readonly ListFormatter<int> Numbers = new();

        public IMessagePackFormatter<T>? GetFormatter<T>() =>
            typeof(T) == typeof(List<int>)
                ? (IMessagePackFormatter<T>)(object)Numbers
                : BuiltinResolver.Instance.GetFormatter<T>();
    }
}
