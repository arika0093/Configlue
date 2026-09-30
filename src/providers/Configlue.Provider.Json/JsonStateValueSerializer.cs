using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Configlue.Provider.Json;

/// <summary>
/// Serializes and deserializes a typed value as a bare JSON value.
/// </summary>
/// <remarks>
/// Unlike <see cref="JsonStateCodec{T}"/>, this primitive does not add document layout, version, or
/// schema metadata to the payload. It is intended for persistence backends such as JSONB columns that
/// store structured JSON directly and keep schema metadata in relational columns.
/// </remarks>
public sealed class JsonStateValueSerializer<T>
{
    private readonly JsonSerializerOptions _options;
    private readonly JsonTypeInfo<T>? _typeInfo;
    private readonly JsonConverter<T>? _converter;

    /// <summary>Creates a serializer that uses the supplied System.Text.Json options.</summary>
    /// <remarks>For trimming and NativeAOT, use the constructor that accepts <see cref="JsonTypeInfo{T}"/>.</remarks>
    [RequiresUnreferencedCode(
        "Reflection-based JSON serialization may require types that trimming removes. Use the JsonTypeInfo constructor for trim-safe serialization."
    )]
    [RequiresDynamicCode(
        "Reflection-based JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT."
    )]
    public JsonStateValueSerializer(JsonSerializerOptions? options = null)
    {
        _options = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        JsonStateCodecOperations.EnsureTypeInfoResolver(_options);
    }

    /// <summary>Creates a serializer that uses source-generated or otherwise preconfigured type metadata.</summary>
    public JsonStateValueSerializer(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        _typeInfo = typeInfo;
        _options = typeInfo.Options;
    }

    /// <summary>Creates a serializer that uses the supplied options and optional generated converter.</summary>
    public JsonStateValueSerializer(JsonSerializerOptions? options, JsonConverter<T>? converter)
    {
        _options = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        JsonStateCodecOperations.EnsureTypeInfoResolver(_options);
        _converter = converter;
    }

    /// <summary>Creates a serializer that uses a generated fragment converter.</summary>
    public static JsonStateValueSerializer<T> FromConverter(JsonConverter<T> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        return new JsonStateValueSerializer<T>(null, converter);
    }

    /// <summary>Serializes <paramref name="value"/> as a bare JSON value.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The options-based constructor carries this requirement. The JsonTypeInfo constructor sets _typeInfo and never enters the reflection fallback."
    )]
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification = "The options-based constructor carries this requirement. The JsonTypeInfo constructor sets _typeInfo and never enters the reflection fallback."
    )]
    public void Serialize(T? value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new Utf8JsonWriter(destination);
        if (_converter is not null)
        {
            if (value is null && !_converter.HandleNull)
            {
                writer.WriteNullValue();
            }
            else
            {
                _converter.Write(writer, value!, _options);
            }
        }
        else if (_typeInfo is null)
        {
            JsonSerializer.Serialize(writer, value, _options);
        }
        else
        {
            JsonSerializer.Serialize<T>(writer, value!, _typeInfo);
        }

        writer.Flush();
    }

    /// <summary>Deserializes a bare JSON value.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The options-based constructor carries this requirement. The JsonTypeInfo constructor sets _typeInfo and never enters the reflection fallback."
    )]
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification = "The options-based constructor carries this requirement. The JsonTypeInfo constructor sets _typeInfo and never enters the reflection fallback."
    )]
    public T? Deserialize(in ReadOnlySequence<byte> source)
    {
        var reader = new Utf8JsonReader(source, JsoncSyntaxTree.ReaderOptions);
        if (_converter is not null)
        {
            if (!reader.Read())
            {
                throw new JsonException("The JSON payload is empty.");
            }

            if (reader.TokenType == JsonTokenType.Null && !_converter.HandleNull)
            {
                return default;
            }

            return _converter.Read(ref reader, typeof(T), _options);
        }

        return _typeInfo is null
            ? JsonSerializer.Deserialize<T>(ref reader, _options)
            : JsonSerializer.Deserialize(ref reader, _typeInfo);
    }
}
