using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using MessagePack.Formatters;

namespace Configlue.Provider.MessagePack;

/// <summary>Serializes and deserializes a typed value as a bare MessagePack value.</summary>
/// <remarks>
/// Unlike <see cref="MessagePackStateCodec{T}"/>, this primitive does not add the schema envelope to
/// the payload. It is intended for persistence backends such as binary columns that store the
/// structured value directly and keep schema metadata in separate columns.
/// </remarks>
/// <typeparam name="T">The value or generated fragment type.</typeparam>
public sealed class MessagePackStateValueSerializer<T>
{
    private readonly MessagePackSerializerOptions _options;
    private readonly IMessagePackFormatter<T>? _formatter;

    /// <summary>Creates a serializer with the default MessagePack options.</summary>
    [RequiresDynamicCode(
        "The default resolver may generate formatters through runtime reflection."
    )]
    public MessagePackStateValueSerializer()
    {
        _options = MessagePackStateCodecDefaults.GetOptions();
        _formatter = ConfiglueMessagePackFragmentRegistry.GetOrNull<T>();
    }

    /// <summary>Creates a serializer with the supplied MessagePack options.</summary>
    public MessagePackStateValueSerializer(MessagePackSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _formatter = ConfiglueMessagePackFragmentRegistry.GetOrNull<T>();
    }

    /// <summary>Serializes <paramref name="value"/> as a bare MessagePack value.</summary>
    public void Serialize(T? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var writer = new MessagePackWriter(destination);
        MessagePackStateCodecOperations.WritePayload(ref writer, value, _options, _formatter);
        writer.Flush();
    }

    /// <summary>Deserializes a bare MessagePack value.</summary>
    public T? Deserialize(in ReadOnlySequence<byte> source)
    {
        var reader = new MessagePackReader(source);
        return MessagePackStateCodecOperations.ReadPayload(ref reader, _options, _formatter);
    }
}
