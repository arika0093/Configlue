using System.Text.Json;

namespace Configlue.DevTools;

/// <summary>
/// JSON Schema generation for the DevTools viewer.
/// </summary>
/// <remarks>
/// <para>
/// Distinct ownership from Monaco/effective-document projection:
/// this emitter reads only the model schema and viewer options and
/// never touches emission contexts, snapshots, or overlay accumulators.
/// </para>
/// <para>
/// Secrets are surfaced as the <c>x-configlue-secret</c> extension only;
/// no secret values flow through schema generation.
/// </para>
/// </remarks>
internal static class ConfiglueViewerSchemaEmitter
{
    public static string BuildSchemaJson(
        ConfiglueModelSchema schema,
        ConfiglueDevToolsViewerOptions options
    )
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", "https://json-schema.org/draft/2020-12/schema");
            writer.WriteString("title", schema.Id);
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            WriteSchemaProperties(writer, schema, options, new HashSet<string>());
            writer.WriteEndObject();
            writer.WriteStartArray("required");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSchemaProperties(
        Utf8JsonWriter writer,
        ConfiglueModelSchema schema,
        ConfiglueDevToolsViewerOptions options,
        HashSet<string> visiting
    )
    {
        foreach (var member in schema.Members)
        {
            if (member.IsDefault)
            {
                continue;
            }

            var wireName = options.NamingPolicy?.ConvertName(member.Name) ?? member.Name;
            writer.WritePropertyName(wireName);
            writer.WriteStartObject();
            WriteSchemaForMember(writer, member, options, visiting);
            if (member.IsSecret)
            {
                writer.WriteBoolean(ConfiglueSecrets.JsonSchemaExtensionName, true);
            }

            if (!string.IsNullOrEmpty(member.EnvironmentVariableName))
            {
                writer.WriteString("description", $"Env: {member.EnvironmentVariableName}");
            }

            writer.WriteEndObject();
        }
    }

    private static void WriteSchemaForMember(
        Utf8JsonWriter writer,
        ConfiglueMemberSchema member,
        ConfiglueDevToolsViewerOptions options,
        HashSet<string> visiting
    )
    {
        var valueType = Nullable.GetUnderlyingType(member.ValueType) ?? member.ValueType;
        if (
            valueType == typeof(string)
            || valueType == typeof(Guid)
            || valueType == typeof(DateTime)
            || valueType == typeof(DateTimeOffset)
        )
        {
            writer.WriteString("type", "string");
            return;
        }

        if (valueType == typeof(bool))
        {
            writer.WriteString("type", "boolean");
            return;
        }

        if (
            valueType == typeof(int)
            || valueType == typeof(long)
            || valueType == typeof(short)
            || valueType == typeof(byte)
            || valueType == typeof(uint)
            || valueType == typeof(ulong)
            || valueType == typeof(ushort)
            || valueType == typeof(sbyte)
        )
        {
            if (valueType.IsEnum)
            {
                WriteEnumSchema(writer, valueType);
                return;
            }

            writer.WriteString("type", "integer");
            return;
        }

        if (valueType.IsEnum)
        {
            WriteEnumSchema(writer, valueType);
            return;
        }

        if (
            valueType == typeof(float)
            || valueType == typeof(double)
            || valueType == typeof(decimal)
        )
        {
            writer.WriteString("type", "number");
            return;
        }

        if (member.NestedSchemaFactory is not null)
        {
            ConfiglueModelSchema? nested = null;
            try
            {
                nested = member.NestedSchemaFactory();
            }
            catch (Exception)
            {
                nested = null;
            }

            if (nested is not null && visiting.Add(nested.Id + "#" + nested.Version))
            {
                try
                {
                    if (
                        typeof(System.Collections.IEnumerable).IsAssignableFrom(valueType)
                        && valueType != typeof(string)
                    )
                    {
                        writer.WriteString("type", "array");
                        writer.WriteStartObject("items");
                        writer.WriteString("type", "object");
                        writer.WriteStartObject("properties");
                        WriteSchemaProperties(writer, nested, options, visiting);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WriteString("type", "object");
                        writer.WriteStartObject("properties");
                        WriteSchemaProperties(writer, nested, options, visiting);
                        writer.WriteEndObject();
                    }
                }
                finally
                {
                    visiting.Remove(nested.Id + "#" + nested.Version);
                }

                return;
            }

            writer.WriteString("type", "object");
            return;
        }

        if (
            typeof(System.Collections.IEnumerable).IsAssignableFrom(valueType)
            && valueType != typeof(string)
        )
        {
            writer.WriteString("type", "array");
            var elementType = GetElementType(valueType);
            if (elementType is not null)
            {
                writer.WriteStartObject("items");
                writer.WriteString("type", MapSimpleType(elementType));
                writer.WriteEndObject();
            }

            return;
        }

        writer.WriteString("type", MapSimpleType(valueType));
    }

    private static void WriteEnumSchema(Utf8JsonWriter writer, Type enumType)
    {
        writer.WriteString("type", "integer");
        try
        {
            var names = Enum.GetNames(enumType);
            var values = Enum.GetValues(enumType);
            writer.WriteStartArray("enum");
            foreach (var value in values)
            {
                writer.WriteNumberValue(Convert.ToInt64(value));
            }

            writer.WriteEndArray();
            writer.WriteString("description", $"Enum: {string.Join(", ", names)}");
        }
        catch (Exception)
        {
            // Enum metadata is best-effort; the type constraint above still applies.
        }
    }

    private static Type? GetElementType(Type collectionType)
    {
        try
        {
            if (collectionType.IsArray)
            {
                return collectionType.GetElementType();
            }

            if (collectionType.IsGenericType)
            {
                var arguments = collectionType.GetGenericArguments();
                if (arguments.Length == 1)
                {
                    return arguments[0];
                }
            }

            foreach (var face in collectionType.GetInterfaces())
            {
                if (face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return face.GetGenericArguments()[0];
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private static string MapSimpleType(Type type)
    {
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        if (unwrapped == typeof(bool))
        {
            return "boolean";
        }

        if (
            unwrapped == typeof(int)
            || unwrapped == typeof(long)
            || unwrapped == typeof(short)
            || unwrapped == typeof(byte)
            || unwrapped == typeof(uint)
            || unwrapped == typeof(ulong)
            || unwrapped == typeof(ushort)
            || unwrapped == typeof(sbyte)
        )
        {
            return "integer";
        }

        if (
            unwrapped == typeof(float)
            || unwrapped == typeof(double)
            || unwrapped == typeof(decimal)
        )
        {
            return "number";
        }

        return "string";
    }
}
