using System.Text.Json;
using System.Text.Json.Nodes;
using Configlue.CompilerServices;

namespace Configlue.DevTools;

/// <summary>
/// Recursive effective-document emission for the DevTools viewer.
/// </summary>
/// <remarks>
/// <para>
/// Owns document/value emission only: walking the schema in order,
/// writing canonical JSON through the emission context, and recording
/// member ranges. Provenance, hover, and marker annotation is delegated
/// to <see cref="ConfiglueViewerProvenanceAnnotator"/>; schema generation
/// lives in <see cref="ConfiglueViewerSchemaEmitter"/>.
/// </para>
/// <para>
/// Secret redaction is enforced at the <see cref="EmitValue"/> boundary via
/// <see cref="ConfiglueViewerEmissionContext.AppendSecretPlaceholder"/>.
/// Redacted members still record ranges and provenance metadata, but secret
/// plaintext never reaches the writer.
/// </para>
/// </remarks>
internal static class ConfiglueViewerDocumentEmitter
{
    public static void EmitObject(
        ConfiglueModelSchema schema,
        JsonObject? node,
        object? modelValue,
        ConfiglueMemberPath pathPrefix,
        string pathNamePrefix,
        bool isSecretAncestor,
        int depth,
        ConfiglueViewerEmissionContext context
    )
    {
        var emitted = 0;
        foreach (var member in schema.Members)
        {
            if (member.IsDefault)
            {
                continue;
            }

            var wireName = ResolveWireName(member, node, context.NamingPolicy, out var childNode);
            if (wireName is null)
            {
                // Ignored/absent members stay absent; projection remains deterministic.
                continue;
            }

            var path = pathPrefix.Append(member.Id);
            var memberPath = string.IsNullOrEmpty(pathNamePrefix)
                ? member.Name
                : pathNamePrefix + "." + member.Name;
            var secret = isSecretAncestor || member.IsSecret;
            var memberValue = TryGetMemberValue(modelValue, member);

            if (emitted > 0)
            {
                context.Writer.Append(",");
            }

            context.Writer.AppendLine();
            context.Writer.AppendIndent(depth);
            var nameStart = context.Writer.Position;
            context.Writer.AppendQuoted(wireName);
            var nameEnd = context.Writer.Position;
            context.Writer.Append(": ");

            var valueStart = context.Writer.Position;
            EmitValue(member, childNode, memberValue, path, memberPath, secret, depth, context);
            var valueEnd = context.Writer.Position;

            context.AddMemberRange(
                memberPath,
                wireName,
                ConfiglueViewerPositionWriter.CaptureRange(nameStart, nameEnd),
                ConfiglueViewerPositionWriter.CaptureRange(valueStart, valueEnd),
                secret
            );

            if (context.Snapshot is not null)
            {
                ConfiglueViewerProvenanceAnnotator.AnnotateMember(
                    context,
                    member,
                    path,
                    memberPath,
                    ConfiglueViewerPositionWriter.CaptureRange(valueStart, valueEnd)
                );
            }

            emitted++;
        }

        if (emitted > 0)
        {
            context.Writer.AppendLine();
            context.Writer.AppendIndent(depth - 1);
        }
    }

    public static void EmitValue(
        ConfiglueMemberSchema member,
        JsonNode? childNode,
        object? memberValue,
        ConfiglueMemberPath path,
        string memberPath,
        bool secret,
        int depth,
        ConfiglueViewerEmissionContext context
    )
    {
        if (secret)
        {
            context.AppendSecretPlaceholder();
            return;
        }

        if (
            childNode is null
            || (childNode is JsonValue nullValue && nullValue.GetValueKind() == JsonValueKind.Null)
        )
        {
            context.Writer.Append("null");
            return;
        }

        if (member.NestedSchemaFactory is not null && childNode is JsonObject childObject)
        {
            var nestedSchema = member.NestedSchemaFactory();
            context.Writer.Append("{");
            EmitObject(
                nestedSchema,
                childObject,
                memberValue,
                path,
                memberPath,
                isSecretAncestor: false,
                depth: depth + 1,
                context
            );
            context.Writer.Append("}");
            return;
        }

        if (childNode is JsonArray array)
        {
            EmitArray(member, array, memberValue, path, memberPath, depth, context);
            return;
        }

        context.Writer.Append(childNode.ToJsonString());
    }

    public static void EmitArray(
        ConfiglueMemberSchema member,
        JsonArray array,
        object? memberValue,
        ConfiglueMemberPath path,
        string memberPath,
        int depth,
        ConfiglueViewerEmissionContext context
    )
    {
        if (array.Count == 0)
        {
            context.Writer.Append("[]");
            return;
        }

        var nestedSchema =
            member.NestedSchemaFactory is not null
            && array.Any(static element => element is JsonObject)
                ? member.NestedSchemaFactory()
                : null;
        context.Writer.Append("[");
        for (var index = 0; index < array.Count; index++)
        {
            if (index > 0)
            {
                context.Writer.Append(",");
            }

            context.Writer.AppendLine();
            context.Writer.AppendIndent(depth + 1);
            var element = array[index];
            var elementPath = $"{memberPath}[{index}]";
            var elementStart = context.Writer.Position;
            if (
                nestedSchema is not null
                && element is JsonObject elementObject
                && memberValue is System.Collections.IEnumerable values
            )
            {
                var elementValue = ElementAt(values, index);
                context.Writer.Append("{");
                EmitObject(
                    nestedSchema,
                    elementObject,
                    elementValue,
                    path,
                    elementPath,
                    isSecretAncestor: false,
                    depth: depth + 2,
                    context
                );
                context.Writer.Append("}");
            }
            else
            {
                context.Writer.Append(element?.ToJsonString() ?? "null");
            }

            var elementEnd = context.Writer.Position;
            // Element name ranges are intentionally empty (arrays have no property keys).
            context.AddMemberRange(
                elementPath,
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ConfiglueViewerPositionWriter.EmptyRangeAt(elementStart),
                ConfiglueViewerPositionWriter.CaptureRange(elementStart, elementEnd),
                member.IsSecret
            );

            if (context.Snapshot is not null)
            {
                ConfiglueViewerProvenanceAnnotator.AnnotateElement(
                    context,
                    path,
                    elementPath,
                    ConfiglueViewerPositionWriter.CaptureRange(elementStart, elementEnd),
                    member.IsSecret
                );
            }
        }

        context.Writer.AppendLine();
        context.Writer.AppendIndent(depth);
        context.Writer.Append("]");
    }

    private static object? TryGetMemberValue(object? modelValue, ConfiglueMemberSchema member)
    {
        if (modelValue is null || member.GetValue is null)
        {
            return null;
        }

        try
        {
            return member.GetValue(modelValue);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ResolveWireName(
        ConfiglueMemberSchema member,
        JsonObject? node,
        JsonNamingPolicy? namingPolicy,
        out JsonNode? child
    )
    {
        child = null;
        if (node is null)
        {
            return namingPolicy?.ConvertName(member.Name) ?? member.Name;
        }

        if (node.TryGetPropertyValue(member.Name, out child))
        {
            return member.Name;
        }

        var converted = namingPolicy?.ConvertName(member.Name);
        if (converted is not null && node.TryGetPropertyValue(converted, out child))
        {
            return converted;
        }

        // Explicit [JsonPropertyName] or other codecs: case-insensitive fallback.
        // This keeps explicit wire names stable without reflection.
        foreach (var entry in node)
        {
            if (string.Equals(entry.Key, member.Name, StringComparison.OrdinalIgnoreCase))
            {
                child = entry.Value;
                return entry.Key;
            }

            if (
                converted is not null
                && string.Equals(entry.Key, converted, StringComparison.OrdinalIgnoreCase)
            )
            {
                child = entry.Value;
                return entry.Key;
            }
        }

        return null;
    }

    private static object? ElementAt(System.Collections.IEnumerable values, int index)
    {
        var current = 0;
        foreach (var item in values)
        {
            if (current == index)
            {
                return item;
            }

            current++;
        }

        return null;
    }
}
