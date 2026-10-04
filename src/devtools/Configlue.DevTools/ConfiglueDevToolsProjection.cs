using System.Text.Json;
using System.Text.Json.Nodes;

namespace Configlue.DevTools;

/// <summary>
/// Builds the canonical JSON projection for DevTools with <c>#244</c> secret redaction.
/// </summary>
/// <remarks>
/// Internal to the DevTools package. Operates on <see cref="ConfiglueModelSchema"/>
/// metadata so nested subtrees and collection elements inherit sensitivity without
/// per-host rules and without reflection.
/// </remarks>
internal static class ConfiglueDevToolsProjection
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string ToRedactedJson<TModel>(TModel value, ConfiglueModelSchema schema)
    {
        var node = JsonSerializer.SerializeToNode(value, CanonicalOptions);
        RedactNode(node, schema);
        return node?.ToJsonString(CanonicalOptions) ?? "null";
    }

    public static string ToSchemaJson(ConfiglueModelSchema schema)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("modelId", schema.Id);
            writer.WriteNumber("modelVersion", schema.Version);
            writer.WriteStartArray("members");
            foreach (var member in schema.Members)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", member.Id);
                writer.WriteString("name", member.Name);
                writer.WriteString("valueType", member.ValueType.FullName ?? member.ValueType.Name);
                writer.WriteBoolean("isSecret", member.IsSecret);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Merges an incoming desired JSON payload over the current value, preserving secrets
    /// whose incoming value is the redacted placeholder.
    /// </summary>
    public static string MergePreservingRedactedSecrets(
        string currentRedactedIgnored,
        string currentJson,
        string incomingJson,
        ConfiglueModelSchema schema
    )
    {
        _ = currentRedactedIgnored;
        var current = JsonNode.Parse(currentJson);
        var incoming = JsonNode.Parse(incomingJson);
        var merged = MergeNode(current, incoming, schema);
        return merged?.ToJsonString(CanonicalOptions) ?? "null";
    }

    public static TModel DeserializeModel<TModel>(string json) =>
        JsonSerializer.Deserialize<TModel>(json, ParseOptions)
        ?? throw new InvalidOperationException(
            "The DevTools payload did not contain a model value."
        );

    private static JsonNode? MergeNode(
        JsonNode? current,
        JsonNode? incoming,
        ConfiglueModelSchema schema
    )
    {
        if (incoming is not JsonObject incomingObject)
        {
            return incoming?.DeepClone();
        }

        var currentObject = current as JsonObject;
        var result = new JsonObject();
        var members = schema.Members.ToDictionary(
            static member => member.Name,
            StringComparer.Ordinal
        );

        foreach (var (name, incomingChild) in incomingObject)
        {
            if (
                members.TryGetValue(name, out var member)
                && member.IsSecret
                && IsRedactedPlaceholder(incomingChild)
            )
            {
                var preserved = currentObject?[name]?.DeepClone();
                if (preserved is not null)
                {
                    result[name] = preserved;
                }
                else if (currentObject is not null && currentObject.ContainsKey(name))
                {
                    result[name] = currentObject[name]?.DeepClone();
                }
                else
                {
                    // No known current secret to preserve; drop the placeholder so a
                    // fake "********" value is never written through normal routing.
                }

                continue;
            }

            if (
                members.TryGetValue(name, out var nestedMember)
                && nestedMember.NestedSchemaFactory is not null
                && incomingChild is JsonObject
            )
            {
                var nestedSchema = nestedMember.NestedSchemaFactory();
                result[name] = MergeNode(currentObject?[name], incomingChild, nestedSchema);
                continue;
            }

            result[name] = incomingChild?.DeepClone();
        }

        return result;
    }

    private static void RedactNode(JsonNode? node, ConfiglueModelSchema schema)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        var members = schema.Members.ToDictionary(
            static member => member.Name,
            StringComparer.Ordinal
        );
        // Case-insensitive fallback for camelCase JSON.
        var membersIgnoreCase = new Dictionary<string, ConfiglueMemberSchema>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var member in schema.Members)
        {
            membersIgnoreCase[member.Name] = member;
        }

        var names = obj.Select(static entry => entry.Key).ToArray();
        foreach (var name in names)
        {
            if (!members.TryGetValue(name, out var member))
            {
                membersIgnoreCase.TryGetValue(name, out member);
            }

            if (member.IsDefault)
            {
                continue;
            }

            if (member.IsSecret)
            {
                obj[name] = ConfiglueSecrets.RedactedText;
                continue;
            }

            if (member.NestedSchemaFactory is not null && obj[name] is JsonObject nested)
            {
                RedactNode(nested, member.NestedSchemaFactory());
            }
        }
    }

    private static bool IsRedactedPlaceholder(JsonNode? node) =>
        node is JsonValue value
        && value.TryGetValue<string>(out var text)
        && string.Equals(text, ConfiglueSecrets.RedactedText, StringComparison.Ordinal);
}
