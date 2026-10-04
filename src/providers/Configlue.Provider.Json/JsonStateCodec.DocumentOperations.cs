using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Configlue.Provider.Json;

internal static partial class JsonStateCodecOperations
{
    private const string MetadataProperty = "$configlue";
    private const string PayloadProperty = "$value";
    private const string SchemaProperty = "$schema";
    private const string DefaultVersionProperty = "$version";

    internal static bool UsesEnvelopeLayout(DocumentLayoutOptions? layout) =>
        (layout?.Layout ?? DocumentLayout.Simple) == DocumentLayout.Detailed;

    internal static void WriteEnvelopeStart(Utf8JsonWriter writer, in StateCodecContext context)
    {
        if (context.Schema is not { } schema || !schema.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                "An envelope requires valid schema metadata."
            );
        }

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
    internal static void EnsureTypeInfoResolver(JsonSerializerOptions options)
    {
        if (options.TypeInfoResolver is not null)
        {
            return;
        }

        if (!JsonSerializer.IsReflectionEnabledByDefault)
        {
            throw new InvalidOperationException(
                "A source-generated JsonSerializerContext must be supplied for JSON serialization when reflection-based JSON serialization is disabled."
            );
        }

        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
    }

    public static ReadOnlySequence<byte> GetPayload(
        in ReadOnlySequence<byte> source,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        var normalizedSource = StripUtf8Bom(source);
        if (!HasMetadataEnvelope(in normalizedSource))
        {
            return StripSimpleDocument(in normalizedSource, layout, serializerOptions);
        }

        using var document = JsonDocument.Parse(normalizedSource, JsoncSyntaxTree.DocumentOptions);
        if (!document.RootElement.TryGetProperty(PayloadProperty, out var payload))
        {
            throw new JsonException(
                $"A '{MetadataProperty}' metadata envelope must contain a '{PayloadProperty}' value."
            );
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            payload.WriteTo(writer);
        }

        return new ReadOnlySequence<byte>(buffer.WrittenMemory);
    }

    public static StateSchemaMetadata? ReadSchemaMetadata(
        in ReadOnlySequence<byte> source,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        var normalizedSource = StripUtf8Bom(source);
        using var document = JsonDocument.Parse(normalizedSource, JsoncSyntaxTree.DocumentOptions);
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
                || versionElement.ValueKind != JsonValueKind.Number
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

        if (!schema.IsValid)
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

    internal static void WriteSchemaReference(Utf8JsonWriter writer, in StateCodecContext context)
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
        var probe = new Utf8JsonReader(source, JsoncSyntaxTree.ReaderOptions);
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

    internal static ReadOnlySequence<byte> StripUtf8Bom(in ReadOnlySequence<byte> source)
    {
        if (source.Length < 3)
        {
            return source;
        }

        var firstSpan = source.First.Span;
        if (firstSpan.Length >= 3)
        {
            return firstSpan[0] == 0xEF && firstSpan[1] == 0xBB && firstSpan[2] == 0xBF
                ? source.Slice(3)
                : source;
        }

        Span<byte> prefix = stackalloc byte[3];
        source.Slice(0, 3).CopyTo(prefix);
        if (prefix[0] != 0xEF || prefix[1] != 0xBB || prefix[2] != 0xBF)
        {
            return source;
        }

        return source.Slice(3);
    }

    private static ReadOnlySequence<byte> StripSimpleDocument(
        in ReadOnlySequence<byte> source,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        using var document = JsonDocument.Parse(source, JsoncSyntaxTree.DocumentOptions);
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
            foreach (var property in root.EnumerateObject())
            {
                if (
                    string.Equals(property.Name, versionName, StringComparison.Ordinal)
                    || string.Equals(property.Name, SchemaProperty, StringComparison.Ordinal)
                )
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return new ReadOnlySequence<byte>(buffer.WrittenMemory);
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
