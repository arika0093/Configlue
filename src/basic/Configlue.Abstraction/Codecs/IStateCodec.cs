using System.Buffers;

namespace Configlue.Codecs;

/// <summary>Serializes arbitrary state values at the byte-buffer boundary.</summary>
public interface IStateCodec
{
    /// <summary>Deserializes a value of the requested type.</summary>
    object? Deserialize(Type type, in ReadOnlySequence<byte> source, in StateCodecContext context);

    /// <summary>Serializes a value into the destination buffer.</summary>
    void Serialize(
        Type type,
        object? value,
        IBufferWriter<byte> destination,
        in StateCodecContext context
    );
}

/// <summary>A typed serialization fast path for a state codec.</summary>
public interface IStateCodec<T>
{
    /// <summary>Deserializes a value from the byte sequence.</summary>
    T? Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context);

    /// <summary>Serializes a value into the destination buffer.</summary>
    void Serialize(T? value, IBufferWriter<byte> destination, in StateCodecContext context);
}

/// <summary>A decoded state value and schema metadata discovered in the same payload pass.</summary>
public readonly record struct StateCodecDecodeResult<T>(T? Value, StateSchemaMetadata? Schema);

/// <summary>Optional single-pass decode capability for codecs with embedded schema metadata.</summary>
public interface IStateCodecWithMetadata<T>
{
    /// <summary>Decodes the value and embedded schema metadata without independently parsing the payload twice.</summary>
    StateCodecDecodeResult<T> DeserializeWithMetadata(
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    );
}
