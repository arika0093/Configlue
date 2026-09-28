using System.Buffers;
using System.Text.Json;

namespace Configlue.Provider.Json;

internal static partial class JsonStateCodecOperations
{
    public static StateSchemaMetadata? ReadSchemaMetadataAndEnvelope(
        in ReadOnlySequence<byte> source,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions,
        out bool isMetadataEnvelope
    )
    {
        isMetadataEnvelope = false;
        var normalizedSource = StripUtf8Bom(source);
        var reader = new Utf8JsonReader(normalizedSource, JsoncSyntaxTree.ReaderOptions);
        if (!reader.Read())
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            if (reader.Read())
            {
                throw new JsonException("Additional JSON content was found after the root value.");
            }

            return null;
        }

        var candidates = GetVersionPropertyCandidates(layout, serializerOptions);
        var selectedCandidate = int.MaxValue;
        int? selectedVersion = null;
        string? selectedVersionName = null;
        var hasSchema = false;
        var hasMetadata = false;
        StateSchemaMetadata? metadataSchema = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a JSON object property.");
            }

            var isMetadata = reader.ValueTextEquals(MetadataProperty);
            isMetadataEnvelope |= isMetadata;
            hasSchema |= reader.ValueTextEquals(SchemaProperty);
            var candidateIndex = FindVersionCandidate(ref reader, candidates, serializerOptions);
            var propertyName = candidateIndex >= 0 ? reader.GetString() : null;
            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of JSON object.");
            }

            if (isMetadata)
            {
                metadataSchema = ReadMetadataSchema(ref reader);
                hasMetadata = true;
            }
            else if (
                candidateIndex >= 0
                && candidateIndex <= selectedCandidate
                && (candidateIndex < selectedCandidate || selectedVersionName == propertyName)
            )
            {
                selectedCandidate = candidateIndex;
                selectedVersionName = propertyName;
                selectedVersion =
                    reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var version)
                        ? version
                        : null;
            }

            reader.Skip();
        }

        if (reader.Read())
        {
            throw new JsonException("Additional JSON content was found after the root object.");
        }

        if (hasMetadata)
        {
            return metadataSchema;
        }

        if (selectedCandidate != int.MaxValue)
        {
            if (selectedVersion is not int version || version < StateSchemaMetadata.InitialVersion)
            {
                throw new JsonException(
                    $"The version property '{selectedVersionName}' must be a positive integer."
                );
            }

            return new StateSchemaMetadata(layout?.ModelId, version);
        }

        return hasSchema || layout?.ModelId is not null
            ? new StateSchemaMetadata(layout?.ModelId, StateSchemaMetadata.InitialVersion)
            : null;
    }

    private static int FindVersionCandidate(
        ref Utf8JsonReader reader,
        string[] candidates,
        JsonSerializerOptions? serializerOptions
    )
    {
        for (var index = 0; index < candidates.Length; index++)
        {
            if (reader.ValueTextEquals(candidates[index]))
            {
                return index;
            }
        }

        if (serializerOptions?.PropertyNameCaseInsensitive == true)
        {
            var propertyName = reader.GetString();
            for (var index = 0; index < candidates.Length; index++)
            {
                if (
                    string.Equals(
                        propertyName,
                        candidates[index],
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static StateSchemaMetadata ReadMetadataSchema(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                "The '$configlue' metadata must contain a positive integer version."
            );
        }

        string? id = null;
        var hasVersion = false;
        var version = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a JSON metadata property.");
            }

            var isVersion = reader.ValueTextEquals("version");
            var isId = reader.ValueTextEquals("id");
            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of JSON metadata.");
            }

            if (isVersion)
            {
                hasVersion =
                    reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out version);
            }
            else if (isId && reader.TokenType == JsonTokenType.String)
            {
                id = reader.GetString();
            }

            reader.Skip();
        }

        if (!hasVersion || version < StateSchemaMetadata.InitialVersion)
        {
            throw new JsonException(
                "The '$configlue' metadata must contain a positive integer version."
            );
        }

        return new StateSchemaMetadata(id, version);
    }

    public static StateSchemaMetadata? ReadSchemaMetadataFromElement(
        JsonElement root,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

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

    public static JsonElement GetPayloadElement(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(MetadataProperty, out _))
        {
            if (!root.TryGetProperty(PayloadProperty, out var payload))
            {
                throw new JsonException(
                    $"A '{MetadataProperty}' metadata envelope must contain a '{PayloadProperty}' value."
                );
            }

            return payload;
        }

        return root;
    }

    public static bool IsMetadataEnvelope(in ReadOnlySequence<byte> source)
    {
        var normalizedSource = StripUtf8Bom(source);
        var reader = new Utf8JsonReader(normalizedSource, JsoncSyntaxTree.ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return false;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a JSON object property.");
            }

            if (reader.ValueTextEquals(MetadataProperty))
            {
                return true;
            }

            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of JSON object.");
            }

            reader.Skip();
        }

        return false;
    }

    public static bool IsMetadataEnvelope(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(MetadataProperty, out _);

    public static ReadOnlyMemory<byte>? GetFilteredPayload(
        JsonElement root,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        var versionName = FindProperty(root, layout, serializerOptions);
        if (versionName is null && !root.TryGetProperty(SchemaProperty, out _))
        {
            return null;
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

        return buffer.WrittenMemory;
    }

    private static string[] GetVersionPropertyCandidates(
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        var candidates = new List<string>();
        AddVersionPropertyCandidates(layout?.VersionProperty ?? DefaultVersionProperty);
        foreach (var fallback in layout?.FallbackVersionProperties ?? ["Version"])
        {
            AddVersionPropertyCandidates(fallback);
        }

        return candidates.ToArray();

        void AddVersionPropertyCandidates(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new ArgumentException(
                    "A version property cannot be empty.",
                    nameof(propertyName)
                );
            }

            candidates.Add(propertyName);
            var convertedName = serializerOptions?.PropertyNamingPolicy?.ConvertName(propertyName);
            if (
                convertedName is not null
                && !string.Equals(convertedName, propertyName, StringComparison.Ordinal)
            )
            {
                candidates.Add(convertedName);
            }
        }
    }
}
