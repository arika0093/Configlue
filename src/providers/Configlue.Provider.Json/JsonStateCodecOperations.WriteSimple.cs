using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Configlue.Provider.Json;

internal static partial class JsonStateCodecOperations
{
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
            || typeInfo.Properties.Any(static property =>
                property.CustomConverter is not null
                || property.IsExtensionData
                || property.NumberHandling is not null
            )
        )
        {
            return false;
        }

        WriteSimpleObjectStart(writer, schema, in context, layout);
        foreach (var property in typeInfo.Properties.OrderBy(static property => property.Order))
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
}
