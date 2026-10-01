using Configlue.Provider.MessagePack;
using MessagePack;
using MessagePack.Formatters;

namespace Configlue.Tests;

/// <summary>MessagePack options that add a formatter for the opaque test scalar type.</summary>
internal static class TestMessagePack
{
    public static MessagePackSerializerOptions Options { get; } =
        MessagePackSerializerOptions.Standard.WithResolver(new AccentResolver());

    private sealed class AccentResolver : IFormatterResolver
    {
        public IMessagePackFormatter<T>? GetFormatter<T>() =>
            typeof(T) == typeof(MessagePackAccentColor)
                ? (IMessagePackFormatter<T>)(object)MessagePackAccentColorFormatter.Instance
                : ConfiglueMessagePackResolver.Instance.GetFormatter<T>();
    }
}

internal sealed class MessagePackAccentColorFormatter : IMessagePackFormatter<MessagePackAccentColor>
{
    public static MessagePackAccentColorFormatter Instance { get; } = new();

    public void Serialize(
        ref MessagePackWriter writer,
        MessagePackAccentColor value,
        MessagePackSerializerOptions options
    )
    {
        writer.WriteArrayHeader(3);
        writer.Write(value.R);
        writer.Write(value.G);
        writer.Write(value.B);
    }

    public MessagePackAccentColor Deserialize(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options
    )
    {
        var count = reader.ReadArrayHeader();
        if (count != 3)
        {
            throw new MessagePackSerializationException(
                "An accent color must be a three-element array."
            );
        }

        return new MessagePackAccentColor(
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte()
        );
    }
}
