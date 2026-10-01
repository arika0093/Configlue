using System.Buffers;
using Configlue;
using Configlue.Provider.MessagePack;
using MessagePack;
using MessagePack.Formatters;

namespace Configlue.Tests;

public sealed class MessagePackResolverTests
{
    [Test]
    public void CustomResolver_IsUsedForOpaqueScalarTypes()
    {
        var resolver = new TrackingResolver();
        var options = MessagePackSerializerOptions.Standard.WithResolver(resolver);
        var codec = new MessagePackStateCodec<MessagePackResolverSettings.Fragment>(options);
        var fragment = MessagePackResolverSettings.Fragment.From(
            new MessagePackResolverSettings { Token = new MessagePackOpaqueToken(11, 22) }
        );
        var buffer = new ArrayBufferWriter<byte>();

        codec.Serialize(fragment, buffer, default);
        var read = codec.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory), default)!;

        read.Token.Value.ShouldBe(new MessagePackOpaqueToken(11, 22));
        resolver.Requested.Contains(typeof(MessagePackOpaqueToken)).ShouldBeTrue();
    }

    private sealed class TrackingResolver : IFormatterResolver
    {
        public HashSet<Type> Requested { get; } = [];

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            Requested.Add(typeof(T));
            if (typeof(T) == typeof(MessagePackOpaqueToken))
            {
                return (IMessagePackFormatter<T>)(object)MessagePackOpaqueTokenFormatter.Instance;
            }

            return ConfiglueMessagePackResolver.Instance.GetFormatter<T>();
        }
    }

    internal sealed class MessagePackOpaqueTokenFormatter
        : IMessagePackFormatter<MessagePackOpaqueToken>
    {
        public static MessagePackOpaqueTokenFormatter Instance { get; } = new();

        public void Serialize(
            ref MessagePackWriter writer,
            MessagePackOpaqueToken value,
            MessagePackSerializerOptions options
        )
        {
            writer.WriteArrayHeader(2);
            writer.Write(value.Left);
            writer.Write(value.Right);
        }

        public MessagePackOpaqueToken Deserialize(
            ref MessagePackReader reader,
            MessagePackSerializerOptions options
        )
        {
            var count = reader.ReadArrayHeader();
            if (count != 2)
            {
                throw new MessagePackSerializationException(
                    "An opaque token must be a two-element array."
                );
            }

            return new MessagePackOpaqueToken(reader.ReadInt32(), reader.ReadInt32());
        }
    }
}
