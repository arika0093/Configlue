using System.Buffers;

namespace Configlue.Codecs;

/// <summary>Serializes arbitrary state values at the byte-buffer boundary.</summary>
/// <remarks>Provider SPI: implemented by codec authors.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
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
/// <remarks>Provider SPI: implemented by codec authors.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IStateCodec<T>
{
    /// <summary>Deserializes a value from the byte sequence.</summary>
    T? Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context);

    /// <summary>Serializes a value into the destination buffer.</summary>
    void Serialize(T? value, IBufferWriter<byte> destination, in StateCodecContext context);
}

/// <summary>A decoded state value and schema metadata discovered in the same payload pass.</summary>
/// <remarks>Advanced provider SPI result.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct StateCodecDecodeResult<T>(T? Value, StateSchemaMetadata? Schema);

/// <summary>Optional single-pass decode capability for codecs with embedded schema metadata.</summary>
/// <remarks>
/// This is an advanced performance contract: a codec works fully without it via the two-step
/// metadata-then-decode path used by the canonical serialized source. Implement it only when the
/// payload already carries schema metadata that can be reported in the same pass, avoiding a second
/// parse. The returned schema is advisory; resource-supplied metadata still takes precedence.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IStateCodecWithMetadata<T>
{
    /// <summary>Decodes the value and embedded schema metadata without independently parsing the payload twice.</summary>
    StateCodecDecodeResult<T> DeserializeWithMetadata(
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    );
}
