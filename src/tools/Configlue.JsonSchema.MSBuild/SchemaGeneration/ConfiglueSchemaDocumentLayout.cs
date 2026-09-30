using System.Text.Json.Nodes;
using Configlue.Codecs;

namespace Configlue.JsonSchema.MSBuild;

internal static class ConfiglueSchemaDocumentLayout
{
    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

    public static JsonObject Apply(
        JsonObject payload,
        string modelId,
        int version,
        string schemaId,
        bool includeSchemaProperty,
        DocumentLayout layout,
        string versionProperty
    )
    {
        var definitions = payload["$defs"]?.DeepClone();
        payload.Remove("$defs");
        payload.Remove("$schema");
        payload.Remove("$id");
        RemoveRequiredProperties(payload);
        if (definitions is not null)
        {
            RemoveRequiredProperties(definitions);
        }

        var root =
            layout == DocumentLayout.Detailed
                ? CreateDetailedDocumentSchema(
                    payload,
                    modelId,
                    version,
                    schemaId,
                    includeSchemaProperty,
                    definitions
                )
                : CreateSimpleDocumentSchema(
                    payload,
                    version,
                    schemaId,
                    includeSchemaProperty,
                    versionProperty,
                    definitions
                );

        return root;
    }

    private static JsonObject CreateSimpleDocumentSchema(
        JsonObject payload,
        int version,
        string schemaId,
        bool includeSchemaProperty,
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
            [versionProperty] = new JsonObject { ["const"] = version },
        };
        if (payload["properties"] is JsonObject modelProperties)
        {
            foreach (var property in modelProperties)
            {
                properties[property.Key] = property.Value?.DeepClone();
            }
        }

        if (includeSchemaProperty)
        {
            properties["$schema"] = new JsonObject { ["type"] = "string" };
        }

        var schema = new JsonObject
        {
            ["$schema"] = Draft202012,
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

    private static JsonObject CreateDetailedDocumentSchema(
        JsonObject payload,
        string modelId,
        int version,
        string schemaId,
        bool includeSchemaProperty,
        JsonNode? definitions
    )
    {
        var properties = new JsonObject
        {
            ["$configlue"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["const"] = modelId },
                    ["version"] = new JsonObject { ["const"] = version },
                },
                ["required"] = new JsonArray("id", "version"),
                ["additionalProperties"] = false,
            },
            ["$value"] = payload,
        };
        if (includeSchemaProperty)
        {
            properties["$schema"] = new JsonObject { ["type"] = "string" };
        }

        var schema = new JsonObject
        {
            ["$schema"] = Draft202012,
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
