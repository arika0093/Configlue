using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Configlue.Provider.Json;

internal static partial class JsonStateCodecOperations
{
    private static readonly ConditionalWeakTable<
        JsonTypeInfo,
        SimpleObjectProperties
    > SimpleProperties = new();

    [RequiresUnreferencedCode("Serialization may require reflected property metadata.")]
    [RequiresDynamicCode("Serialization may require runtime-generated JSON metadata.")]
    internal static bool TryWriteSimpleObjectPayload(
        Utf8JsonWriter writer,
        object? value,
        Type declaredType,
        JsonConverter? converter,
        JsonTypeInfo? typeInfo,
        JsonSerializerOptions options,
        StateSchemaMetadata schema,
        in StateCodecContext context,
        DocumentLayoutOptions? layout
    )
    {
        if (
            (layout?.Layout ?? DocumentLayout.Simple) != DocumentLayout.Simple
            || value is null
            || !schema.IsValid
        )
        {
            return false;
        }

        // System.Text.Json invokes IJsonOnSerializing/IJsonOnSerialized (mapped to
        // JsonTypeInfo.OnSerializing/OnSerialized) via JsonSerializer. The fast paths
        // below bypass JsonSerializer for the root object, so fall back when root
        // callbacks are configured. The interface check covers cases where the
        // caller could not supply type metadata (raw payload-writer path).
        if (value is IJsonOnSerializing || value is IJsonOnSerialized)
        {
            return false;
        }

        var effectiveTypeInfo = typeInfo ?? TryGetTypeInfoForCallbacks(options, declaredType);
        if (
            effectiveTypeInfo is not null
            && (
                effectiveTypeInfo.OnSerializing is not null
                || effectiveTypeInfo.OnSerialized is not null
            )
        )
        {
            return false;
        }

        if (converter is IJsonObjectPayloadWriter generatedWriter)
        {
            WriteSimpleObjectStart(writer, schema, in context, layout);
            generatedWriter.WriteObjectPayloadProperties(writer, value, options);
            writer.WriteEndObject();
            return true;
        }

        if (
            typeInfo is null
            || typeInfo.Kind != JsonTypeInfoKind.Object
            || typeInfo.Type != declaredType
            || value.GetType() != declaredType
            || typeInfo.PolymorphismOptions is not null
            || options.ReferenceHandler is not null
            || options.DefaultIgnoreCondition != JsonIgnoreCondition.Never
            || options.IgnoreReadOnlyProperties
            || options.IgnoreReadOnlyFields
            || options.NumberHandling != JsonNumberHandling.Strict
        )
        {
            return false;
        }

        // Only immutable metadata can retain a sorted plan. A caller-provided mutable
        // JsonTypeInfo must be inspected again on each write, including eligibility.
        var properties =
            typeInfo.IsReadOnly && options.IsReadOnly
                ? SimpleProperties.GetValue(
                    typeInfo,
                    static info => CreateSimpleObjectProperties(info)
                )
                : CreateSimpleObjectProperties(typeInfo);
        if (properties.Ordered is null)
        {
            return false;
        }

        WriteSimpleObjectStart(writer, schema, in context, layout);
        foreach (var property in properties.Ordered)
        {
            if (property.Get is null)
            {
                continue;
            }

            var propertyValue = property.Get(value);
            if (
                property.ShouldSerialize is { } shouldSerialize
                && !shouldSerialize(value, propertyValue)
            )
            {
                continue;
            }

            writer.WritePropertyName(property.Name);
            JsonSerializer.Serialize(writer, propertyValue, property.PropertyType, options);
        }
        writer.WriteEndObject();
        return true;
    }

    private static SimpleObjectProperties CreateSimpleObjectProperties(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (
                property.CustomConverter is not null
                || property.IsExtensionData
                || property.NumberHandling is not null
            )
            {
                return new(null);
            }
        }
        return new(typeInfo.Properties.OrderBy(static property => property.Order).ToArray());
    }

    private sealed class SimpleObjectProperties(JsonPropertyInfo[]? ordered)
    {
        public JsonPropertyInfo[]? Ordered { get; } = ordered;
    }

    private static void WriteSimpleObjectStart(
        Utf8JsonWriter writer,
        StateSchemaMetadata schema,
        in StateCodecContext context,
        DocumentLayoutOptions? layout
    )
    {
        writer.WriteStartObject();
        writer.WriteNumber(layout?.VersionProperty ?? DefaultVersionProperty, schema.Version);
        if (context.SchemaReferenceBaseUri is not null)
        {
            WriteSchemaReference(writer, in context);
        }
    }

    internal static JsonTypeInfo? TryGetTypeInfoForCallbacks(
        JsonSerializerOptions options,
        Type declaredType
    )
    {
        try
        {
            return options.GetTypeInfo(declaredType);
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is InvalidOperationException)
        {
            return null;
        }
    }
}
