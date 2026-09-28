using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Configlue.JsonSchema;

public static partial class JsonSchemaGenerator
{
    private sealed class JsonSchemaNodeTransformer
    {
        private readonly IJsonTypeInfoResolver _resolver;
        private readonly JsonSerializerOptions _jsonOptions;
        private int _nextEmbeddedSchemaId;

        public JsonSchemaNodeTransformer(
            IJsonTypeInfoResolver resolver,
            JsonSerializerOptions jsonOptions
        )
        {
            _resolver = resolver;
            _jsonOptions = jsonOptions;
            Options = new JsonSchemaExporterOptions { TransformSchemaNode = TransformSchemaNode };
        }

        public JsonSchemaExporterOptions Options { get; }

        private JsonNode TransformSchemaNode(JsonSchemaExporterContext context, JsonNode node)
        {
            var schemaOverride = GetAttributes<JsonSchemaOverrideAttribute>(
                    context.PropertyInfo?.AttributeProvider
                )
                .FirstOrDefault();
            var oneOf = GetAttributes<JsonSchemaOneOfAttribute>(
                    context.PropertyInfo?.AttributeProvider
                )
                .FirstOrDefault();
            if (schemaOverride is not null && oneOf is not null)
                throw new JsonException(
                    "A property cannot declare both a JSON Schema override and a oneOf schema."
                );

            var isBooleanSchema =
                node is JsonValue converterSchema && converterSchema.TryGetValue<bool>(out _);
            if ((schemaOverride is not null || oneOf is not null) && !isBooleanSchema)
                throw new JsonException(
                    "JSON Schema override attributes can only be used with properties that export a boolean schema."
                );

            if (isBooleanSchema)
            {
                if (schemaOverride is not null)
                {
                    var replacement = JsonNode.Parse(schemaOverride.Schema);
                    if (
                        replacement is not JsonObject
                        && (
                            replacement is not JsonValue replacementValue
                            || !replacementValue.TryGetValue<bool>(out _)
                        )
                    )
                        throw new JsonException(
                            "A JSON Schema override must be a JSON object or boolean schema."
                        );
                    node = replacement;
                }
                else if (oneOf is not null)
                {
                    if (
                        oneOf.Types.Length == 0
                        || oneOf.Types.Any(type => type is null)
                        || oneOf.Types.Distinct().Count() != oneOf.Types.Length
                    )
                        throw new JsonException(
                            "A oneOf schema must specify distinct, non-null types."
                        );

                    var alternatives = new JsonArray();
                    foreach (var alternativeType in oneOf.Types)
                    {
                        var alternativeTypeInfo = _resolver.GetTypeInfo(
                            alternativeType,
                            _jsonOptions
                        );
                        var alternativeSchema = alternativeTypeInfo is null
                            ? CreatePrimitiveSchema(alternativeType)
                            : JsonSchemaExporter.GetJsonSchemaAsNode(alternativeTypeInfo, Options);
                        if (alternativeSchema is null)
                            throw new JsonException(
                                $"The configured JSON type-info resolver does not provide metadata for oneOf type '{alternativeType.FullName}', and the type is not a supported primitive."
                            );

                        if (
                            alternativeSchema is JsonObject alternativeObject
                            && alternativeObject["$defs"] is JsonObject
                        )
                            alternativeObject["$id"] ??=
                                $"urn:configlue:oneof:{_nextEmbeddedSchemaId++}";
                        alternatives.Add(alternativeSchema);
                    }
                    node = new JsonObject { ["oneOf"] = alternatives };
                }
            }

            if (node is not JsonObject schema)
            {
                if (node is not JsonValue value || !value.TryGetValue<bool>(out var booleanSchema))
                    return node;

                schema = booleanSchema
                    ? new JsonObject()
                    : new JsonObject { ["not"] = new JsonObject() };
            }

            var description = GetAttributes<DescriptionAttribute>(
                    context.PropertyInfo?.AttributeProvider
                )
                .FirstOrDefault();
            if (description is not null)
                schema["description"] = description.Description;
            else
            {
                var displayDescription = GetAttributes<DisplayAttribute>(
                        context.PropertyInfo?.AttributeProvider
                    )
                    .FirstOrDefault()
                    ?.Description;
                if (displayDescription is not null)
                    schema["description"] = displayDescription;
            }

            var displayName = GetAttributes<DisplayAttribute>(
                    context.PropertyInfo?.AttributeProvider
                )
                .FirstOrDefault()
                ?.Name;
            if (displayName is not null)
                schema["title"] = displayName;

            JsonSchemaValidationAttributeMapper.AddRequiredProperties(schema, context.TypeInfo);
            foreach (
                var attribute in GetAttributes<ValidationAttribute>(
                    context.PropertyInfo?.AttributeProvider
                )
            )
                JsonSchemaValidationAttributeMapper.Apply(schema, attribute, context.TypeInfo);
            return schema;
        }

        private static JsonNode? CreatePrimitiveSchema(Type type)
        {
            var nullableType = Nullable.GetUnderlyingType(type);
            if (nullableType is not null)
                type = nullableType;

            if (type.IsEnum)
                return null;

            var schemaType = Type.GetTypeCode(type) switch
            {
                TypeCode.String or TypeCode.Char => "string",
                TypeCode.Boolean => "boolean",
                TypeCode.Byte
                or TypeCode.SByte
                or TypeCode.Int16
                or TypeCode.UInt16
                or TypeCode.Int32
                or TypeCode.UInt32
                or TypeCode.Int64
                or TypeCode.UInt64 => "integer",
                TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "number",
                _ => null,
            };
            if (schemaType is null)
                return null;

            var schema = new JsonObject { ["type"] = schemaType };
            if (nullableType is not null)
                schema["type"] = new JsonArray(schemaType, "null");
            return schema;
        }
    }

    private static IEnumerable<TAttribute> GetAttributes<TAttribute>(
        ICustomAttributeProvider? attributeProvider
    )
        where TAttribute : Attribute =>
        attributeProvider
            ?.GetCustomAttributes(typeof(TAttribute), inherit: true)
            .OfType<TAttribute>()
        ?? [];

    private static JsonNode CreatePersistedDocumentSchema(
        JsonNode exportedModelSchema,
        ConfiglueModelSchema model,
        string schemaId,
        bool includeConfigurationSchema,
        DocumentLayoutOptions? documentLayout
    )
    {
        if (exportedModelSchema is not JsonObject payload)
        {
            throw new InvalidOperationException("The exported JSON schema root must be an object.");
        }

        var definitions = payload["$defs"]?.DeepClone();
        if (definitions is not null)
        {
            RemoveRequiredProperties(definitions);
        }

        payload.Remove("$schema");
        payload.Remove("$id");
        payload.Remove("$defs");
        RemoveRequiredProperties(payload);

        if ((documentLayout?.Layout ?? DocumentLayout.Simple) == DocumentLayout.Simple)
        {
            return CreateSimpleDocumentSchema(
                payload,
                model,
                schemaId,
                includeConfigurationSchema,
                documentLayout?.VersionProperty ?? "$version",
                definitions
            );
        }

        var properties = new JsonObject
        {
            ["$configlue"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["const"] = model.Id },
                    ["version"] = new JsonObject { ["const"] = model.Version },
                },
                ["required"] = new JsonArray("id", "version"),
                ["additionalProperties"] = false,
            },
            ["$value"] = payload,
        };
        if (includeConfigurationSchema)
        {
            properties["$schema"] = new JsonObject { ["type"] = "string" };
        }

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = schemaId,
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("$configlue", "$value"),
            ["additionalProperties"] = false,
        };
        if (definitions is not null)
        {
            schema["$defs"] = definitions;
        }

        return schema;
    }

    private static JsonNode CreateSimpleDocumentSchema(
        JsonObject payload,
        ConfiglueModelSchema model,
        string schemaId,
        bool includeConfigurationSchema,
        string versionProperty,
        JsonNode? definitions
    )
    {
        if (string.IsNullOrWhiteSpace(versionProperty))
        {
            throw new ArgumentException(
                "A version property cannot be empty.",
                nameof(versionProperty)
            );
        }

        var properties = new JsonObject
        {
            [versionProperty] = new JsonObject { ["const"] = model.Version },
        };
        if (payload["properties"] is JsonObject modelProperties)
        {
            foreach (var property in modelProperties)
            {
                properties.Add(property.Key, property.Value?.DeepClone());
            }
        }

        if (includeConfigurationSchema)
        {
            properties["$schema"] = new JsonObject { ["type"] = "string" };
        }

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = schemaId,
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(versionProperty),
            ["additionalProperties"] = false,
        };
        if (definitions is not null)
        {
            schema["$defs"] = definitions;
        }

        return schema;
    }

    private static void RemoveRequiredProperties(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            jsonObject.Remove("required");
            foreach (var child in jsonObject.Where(static item => item.Value is not null))
            {
                RemoveRequiredProperties(child.Value!);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var child in jsonArray.Where(static item => item is not null))
            {
                RemoveRequiredProperties(child!);
            }
        }
    }
}
