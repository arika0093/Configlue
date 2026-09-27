using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Configlue.Provider.Json;

/// <summary>A JSON state codec with optional schema metadata stored beside the payload.</summary>
public sealed class JsonStateCodec
    : IStateCodec,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly JsonSerializerOptions _options;
    private readonly DocumentLayoutOptions? _layout;

    /// <summary>Creates a codec with the supplied System.Text.Json options.</summary>
    public JsonStateCodec(
        JsonSerializerOptions? options = null,
        DocumentLayoutOptions? documentLayout = null
    )
    {
        _options = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        _layout = documentLayout;
    }

    /// <inheritdoc />
    public object? Deserialize(
        Type type,
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        var payload = JsonStateCodecOperations.GetPayload(in source, _layout, _options, out _);
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
            ? new StateCodecContext(metadata, context.Services, context.SchemaReferenceBaseUri)
            : context;
        JsonStateCodecOperations.WritePayload(
            raw.WrittenMemory,
            destination,
            in effectiveContext,
            _layout
        );
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        JsonStateCodecOperations.ReadSchemaMetadata(in source, _layout, _options);

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) => exception is JsonException;
}

/// <summary>A typed JSON fast path for a state codec.</summary>
public sealed class JsonStateCodec<T>
    : IStateCodec<T>,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly JsonSerializerOptions _options;
    private readonly JsonTypeInfo<T>? _typeInfo;
    private readonly JsonConverter<T>? _converter;
    private readonly DocumentLayoutOptions? _layout;

    /// <summary>Creates a reflection-based codec that uses the supplied options.</summary>
    /// <remarks>For trimming and NativeAOT, use the constructor that accepts <see cref="JsonTypeInfo{T}"/>.</remarks>
    [RequiresUnreferencedCode(
        "Reflection-based JSON serialization may require types that trimming removes. Use the JsonTypeInfo constructor for trim-safe serialization."
    )]
    [RequiresDynamicCode(
        "Reflection-based JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT."
    )]
    public JsonStateCodec(
        JsonSerializerOptions? options = null,
        DocumentLayoutOptions? documentLayout = null
    )
    {
        _options = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        _layout = documentLayout;
    }

    /// <summary>Creates a codec that uses source-generated or otherwise preconfigured type metadata.</summary>
    public JsonStateCodec(JsonTypeInfo<T> typeInfo, DocumentLayoutOptions? documentLayout = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        _typeInfo = typeInfo;
        _options = typeInfo.Options;
        _layout = documentLayout;
    }

    internal JsonStateCodec(
        JsonSerializerOptions? options,
        JsonConverter<T>? converter,
        DocumentLayoutOptions? documentLayout
    )
    {
        _options = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        EnsureTypeInfoResolver(_options);
        _converter = converter;
        _layout = documentLayout;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The reflection resolver is created only when reflection-based serialization is enabled. NativeAOT applications must supply a source-generated resolver, which bypasses this branch."
    )]
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification = "The reflection resolver is created only when reflection-based serialization is enabled. NativeAOT applications must supply a source-generated resolver, which bypasses this branch."
    )]
    private static void EnsureTypeInfoResolver(JsonSerializerOptions options)
    {
        if (options.TypeInfoResolver is not null)
        {
            return;
        }

        if (!JsonSerializer.IsReflectionEnabledByDefault)
        {
            throw new InvalidOperationException(
                "A source-generated JsonSerializerContext must be supplied for JSON facade sources when reflection-based JSON serialization is disabled."
            );
        }

        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
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
        var payload = JsonStateCodecOperations.GetPayload(in source, _layout, _options, out _);
        var reader = new Utf8JsonReader(payload);
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

        var schema =
            context.Schema
            ?? (value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null);
        var effectiveContext = schema is { } metadata
            ? new StateCodecContext(metadata, context.Services, context.SchemaReferenceBaseUri)
            : context;
        JsonStateCodecOperations.WritePayload(
            raw.WrittenMemory,
            destination,
            in effectiveContext,
            _layout
        );
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        JsonStateCodecOperations.ReadSchemaMetadata(in source, _layout, _options);

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) => exception is JsonException;
}

internal static class JsonStateCodecOperations
{
    private const string MetadataProperty = "$configlue";
    private const string PayloadProperty = "$value";
    private const string SchemaProperty = "$schema";
    private const string DefaultVersionProperty = "$version";

    public static ReadOnlySequence<byte> GetPayload(
        in ReadOnlySequence<byte> source,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions,
        out byte[]? ownedPayload
    )
    {
        ownedPayload = null;
        if (!HasMetadataEnvelope(in source))
        {
            return StripSimpleDocument(in source, layout, serializerOptions, out ownedPayload);
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

    public static StateSchemaMetadata? ReadSchemaMetadata(
        in ReadOnlySequence<byte> source,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        using var document = JsonDocument.Parse(source);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        if (root.TryGetProperty(MetadataProperty, out var metadata))
        {
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

        return ReadSimpleVersion(root, layout, serializerOptions);
    }

    public static void WritePayload(
        ReadOnlyMemory<byte> serializedValue,
        IBufferWriter<byte> destination,
        in StateCodecContext context,
        DocumentLayoutOptions? layout
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
        if (
            (layout?.Layout ?? DocumentLayout.Simple) == DocumentLayout.Simple
            && document.RootElement.ValueKind == JsonValueKind.Object
        )
        {
            WriteSimplePayload(document.RootElement, schema, destination, in context, layout);
            return;
        }

        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        WriteSchemaReference(writer, in context);
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

    private static void WriteSimplePayload(
        JsonElement payload,
        StateSchemaMetadata schema,
        IBufferWriter<byte> destination,
        in StateCodecContext context,
        DocumentLayoutOptions? layout
    )
    {
        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteNumber(layout?.VersionProperty ?? DefaultVersionProperty, schema.Version);
        if (context.SchemaReferenceBaseUri is not null)
        {
            WriteSchemaReference(writer, in context);
        }

        foreach (var property in payload.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteSchemaReference(Utf8JsonWriter writer, in StateCodecContext context)
    {
        if (context.SchemaReferenceBaseUri is not { } schemaReferenceBaseUri)
        {
            return;
        }

        if (context.Schema is not { } referencedSchema)
        {
            throw new InvalidOperationException(
                "A schema reference base URI requires schema metadata."
            );
        }

        writer.WriteString(
            SchemaProperty,
            StateSchemaReference.CreateUri(schemaReferenceBaseUri, referencedSchema)
        );
    }

    private static bool HasMetadataEnvelope(in ReadOnlySequence<byte> source)
    {
        var probe = new Utf8JsonReader(source);
        if (!probe.Read() || probe.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        while (probe.Read())
        {
            if (probe.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (probe.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            if (probe.ValueTextEquals(MetadataProperty))
            {
                return true;
            }

            if (!probe.Read())
            {
                return false;
            }

            probe.Skip();
        }

        return false;
    }

    private static ReadOnlySequence<byte> StripSimpleDocument(
        in ReadOnlySequence<byte> source,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions,
        out byte[]? ownedPayload
    )
    {
        ownedPayload = null;
        using var document = JsonDocument.Parse(source);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return source;
        }

        var root = document.RootElement;
        var versionName = FindProperty(root, layout, serializerOptions);
        if (versionName is null && !root.TryGetProperty(SchemaProperty, out _))
        {
            return source;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (
                var property in root.EnumerateObject()
                    .Where(property =>
                        !string.Equals(property.Name, versionName, StringComparison.Ordinal)
                        && !string.Equals(property.Name, SchemaProperty, StringComparison.Ordinal)
                    )
            )
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        ownedPayload = buffer.WrittenMemory.ToArray();
        return new ReadOnlySequence<byte>(ownedPayload);
    }

    private static StateSchemaMetadata? ReadSimpleVersion(
        JsonElement root,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        var modelId = layout?.ModelId;
        var versionName = FindProperty(root, layout, serializerOptions);
        if (versionName is null)
        {
            // A schema-annotated document without a version defaults to version 1. A document
            // without any marker keeps the legacy bare behavior, unless a model ID opts the
            // reader into legacy version attribution for migration.
            return root.TryGetProperty(SchemaProperty, out _) || modelId is not null
                ? new StateSchemaMetadata(modelId, StateSchemaMetadata.InitialVersion)
                : null;
        }

        if (
            !root.TryGetProperty(versionName, out var versionValue)
            || versionValue.ValueKind != JsonValueKind.Number
            || !versionValue.TryGetInt32(out var version)
            || version < StateSchemaMetadata.InitialVersion
        )
        {
            throw new JsonException(
                $"The version property '{versionName}' must be a positive integer."
            );
        }

        return new StateSchemaMetadata(modelId, version);
    }

    private static string? FindProperty(
        JsonElement root,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        if (
            FindProperty(
                root,
                layout?.VersionProperty ?? DefaultVersionProperty,
                serializerOptions
            ) is
            { } versionName
        )
        {
            return versionName;
        }

        var fallbacks = layout?.FallbackVersionProperties ?? ["Version"];
        foreach (var fallback in fallbacks)
        {
            if (FindProperty(root, fallback, serializerOptions) is { } fallbackName)
            {
                return fallbackName;
            }
        }

        return null;
    }

    private static string? FindProperty(
        JsonElement root,
        string propertyName,
        JsonSerializerOptions? serializerOptions
    )
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new ArgumentException(
                "A version property cannot be empty.",
                nameof(propertyName)
            );
        }

        if (root.TryGetProperty(propertyName, out _))
        {
            return propertyName;
        }

        var convertedName = serializerOptions?.PropertyNamingPolicy?.ConvertName(propertyName);
        if (
            convertedName is not null
            && !string.Equals(convertedName, propertyName, StringComparison.Ordinal)
            && root.TryGetProperty(convertedName, out _)
        )
        {
            return convertedName;
        }

        if (serializerOptions?.PropertyNameCaseInsensitive == true)
        {
            return root.EnumerateObject()
                .Select(property => property.Name)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate, propertyName, StringComparison.OrdinalIgnoreCase)
                    || (
                        convertedName is not null
                        && string.Equals(
                            candidate,
                            convertedName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                );
        }

        return null;
    }
}
