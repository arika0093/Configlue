using System.Text.Json;

namespace Configlue.Provider.Json;

/// <summary>Writes the properties of an object payload into an already-open JSON object.</summary>
/// <remarks>
/// Generated fragment converters implement this hook so schema metadata can be written before the payload
/// without serializing the payload into a temporary buffer.
/// </remarks>
public interface IJsonObjectPayloadWriter
{
    /// <summary>Writes payload properties without opening or closing the surrounding JSON object.</summary>
    void WriteObjectPayloadProperties(
        Utf8JsonWriter writer,
        object value,
        JsonSerializerOptions options
    );
}
