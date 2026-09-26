using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Configlue.Provider.Json;

/// <summary>A JSON state codec with optional schema metadata stored beside the payload.</summary>
public sealed class JsonStateCodec : IStateCodec, IStateSchemaMetadataReader
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Creates a codec with the supplied System.Text.Json options.</summary>
    public JsonStateCodec(JsonSerializerOptions? options = null)
    {
        _options = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
    }

    /// <inheritdoc />
    public object? Deserialize(
        Type type,
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        var payload = JsonStateCodecOperations.GetPayload(in source, out _);
        var reader = new Utf8JsonReader(payload);
        return JsonSerializer.Deserialize(ref reader, type, _options);
    }

    /// <inheritdoc />
    public void Serialize(
        Type type,
        object? value,
        IBufferWriter<byte> destination,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(destination);
        var raw = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(raw))
        {
            JsonSerializer.Serialize(writer, value, type, _options);
            writer.Flush();
        }

        var schema =
            context.Schema
            ?? (value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null);
        var effectiveContext = schema is { } metadata
            ? new StateCodecContext(metadata, context.Services)
            : context;
        JsonStateCodecOperations.WritePayload(raw.WrittenMemory, destination, in effectiveContext);
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        JsonStateCodecOperations.ReadSchemaMetadata(in source);
}

/// <summary>A typed JSON fast path for a state codec.</summary>
public sealed class JsonStateCodec<T> : IStateCodec<T>, IStateSchemaMetadataReader
{
    private readonly JsonSerializerOptions _options;
    private readonly JsonTypeInfo<T>? _typeInfo;

    /// <summary>Creates a reflection-based codec that uses the supplied options.</summary>
    /// <remarks>For trimming and NativeAOT, use the constructor that accepts <see cref="JsonTypeInfo{T}"/>.</remarks>
    [RequiresUnreferencedCode(
        "Reflection-based JSON serialization may require types that trimming removes. Use the JsonTypeInfo constructor for trim-safe serialization."
    )]
    [RequiresDynamicCode(
        "Reflection-based JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT."
    )]
    public JsonStateCodec(JsonSerializerOptions? options = null)
    {
        _options = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
    }

    /// <summary>Creates a codec that uses source-generated or otherwise preconfigured type metadata.</summary>
    public JsonStateCodec(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        _typeInfo = typeInfo;
        _options = typeInfo.Options;
    }

    /// <inheritdoc />
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
    public T? Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context)
    {
        var payload = JsonStateCodecOperations.GetPayload(in source, out _);
        var reader = new Utf8JsonReader(payload);
        return _typeInfo is null
            ? JsonSerializer.Deserialize<T>(ref reader, _options)
            : JsonSerializer.Deserialize(ref reader, _typeInfo);
    }

    /// <inheritdoc />
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
    public void Serialize(T? value, IBufferWriter<byte> destination, in StateCodecContext context)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var raw = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(raw))
        {
            if (_typeInfo is null)
            {
                JsonSerializer.Serialize(writer, value, _options);
            }
            else
            {
                JsonSerializer.Serialize<T>(writer, value!, _typeInfo);
            }

            writer.Flush();
        }

        var schema =
            context.Schema
            ?? (value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null);
        var effectiveContext = schema is { } metadata
            ? new StateCodecContext(metadata, context.Services)
            : context;
        JsonStateCodecOperations.WritePayload(raw.WrittenMemory, destination, in effectiveContext);
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        JsonStateCodecOperations.ReadSchemaMetadata(in source);
}

internal static class JsonStateCodecOperations
{
    private const string MetadataProperty = "$configlue";
    private const string PayloadProperty = "$value";

    public static ReadOnlySequence<byte> GetPayload(
        in ReadOnlySequence<byte> source,
        out byte[]? ownedPayload
    )
    {
        ownedPayload = null;
        var probe = new Utf8JsonReader(source);
        if (
            !probe.Read()
            || probe.TokenType != JsonTokenType.StartObject
            || !probe.Read()
            || probe.TokenType != JsonTokenType.PropertyName
            || !probe.ValueTextEquals(MetadataProperty)
        )
        {
            return source;
        }

        using var document = JsonDocument.Parse(source);
        if (!document.RootElement.TryGetProperty(PayloadProperty, out var payload))
        {
            throw new JsonException(
                $"A '{MetadataProperty}' metadata envelope must contain a '{PayloadProperty}' value."
            );
        }

        ownedPayload = System.Text.Encoding.UTF8.GetBytes(payload.GetRawText());
        return new ReadOnlySequence<byte>(ownedPayload);
    }

    public static StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source)
    {
        using var document = JsonDocument.Parse(source);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!document.RootElement.TryGetProperty(MetadataProperty, out var metadata))
        {
            return null;
        }

        if (
            metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("version", out var versionElement)
            || !versionElement.TryGetInt32(out var version)
            || version < StateSchemaMetadata.InitialVersion
        )
        {
            throw new JsonException(
                $"The '{MetadataProperty}' metadata must contain a positive integer version."
            );
        }

        var id =
            metadata.TryGetProperty("id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
        return new StateSchemaMetadata(id, version);
    }

    public static void WritePayload(
        ReadOnlyMemory<byte> serializedValue,
        IBufferWriter<byte> destination,
        in StateCodecContext context
    )
    {
        if (context.Schema is not { } schema)
        {
            serializedValue.Span.CopyTo(destination.GetSpan(serializedValue.Length));
            destination.Advance(serializedValue.Length);
            return;
        }

        if (schema.Version < StateSchemaMetadata.InitialVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                "Schema versions must be positive."
            );
        }

        using var document = JsonDocument.Parse(serializedValue);
        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WritePropertyName(MetadataProperty);
        writer.WriteStartObject();
        if (schema.ModelId is not null)
        {
            writer.WriteString("id", schema.ModelId);
        }

        writer.WriteNumber("version", schema.Version);
        writer.WriteEndObject();
        writer.WritePropertyName(PayloadProperty);
        document.RootElement.WriteTo(writer);
        writer.WriteEndObject();
        writer.Flush();
    }
}
