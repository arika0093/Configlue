using System.Text.Json;

namespace Configlue.Provider.Json;

internal sealed class JsoncSyntaxTree
{
    private JsoncSyntaxTree(byte[] source, JsoncValueNode root)
    {
        Source = source;
        Root = root;
    }

    internal byte[] Source { get; }

    internal JsoncValueNode Root { get; }

    internal ReadOnlyMemory<byte> GetRawText(JsoncValueNode node) =>
        Source.AsMemory(node.Start, node.End - node.Start);

    internal JsoncValueNode? GetPath(IReadOnlyList<string> path) => GetPath(path, path.Count);

    internal JsoncValueNode? GetPath(IReadOnlyList<string> path, int length)
    {
        var current = Root;
        for (var index = 0; index < length; index++)
        {
            var segment = path[index];
            if (current.Kind != JsonValueKind.Object)
            {
                throw new JsonException(
                    $"JSON section path '{string.Join(":", path.Take(length).ToArray())}' crosses a non-object value at '{segment}'."
                );
            }

            var properties = current.Properties!;
            JsoncPropertyNode? property = null;
            for (var propertyIndex = 0; propertyIndex < properties.Count; propertyIndex++)
            {
                if (
                    string.Equals(properties[propertyIndex].Name, segment, StringComparison.Ordinal)
                )
                {
                    property = properties[propertyIndex];
                    break;
                }
            }

            if (property is null)
            {
                return null;
            }

            current = property.Value;
        }

        return current;
    }

    internal static JsoncSyntaxTree Parse(byte[] source)
    {
        var offset = source.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        var reader = new Utf8JsonReader(source.AsSpan(offset), ReaderOptions);
        if (!reader.Read())
        {
            throw new JsonException("The JSON document is empty.");
        }

        var root = ReadValue(ref reader, source, offset);
        if (reader.Read())
        {
            throw new JsonException("The JSON document contains trailing content.");
        }

        return new JsoncSyntaxTree(source, root);
    }

    internal static JsonDocumentOptions DocumentOptions { get; } =
        new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    internal static JsonReaderOptions ReaderOptions { get; } =
        new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static JsoncValueNode ReadValue(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        var start = checked((int)reader.TokenStartIndex) + offset;
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var value = new JsoncValueNode(JsonValueKind.Object, start)
                {
                    OpenEnd = checked((int)reader.BytesConsumed) + offset,
                };
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        value.CloseStart = checked((int)reader.TokenStartIndex) + offset;
                        value.End = checked((int)reader.BytesConsumed) + offset;
                        SetCommas(value, source);
                        return value;
                    }

                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        throw new JsonException("Expected an object property name.");
                    }

                    var nameStart = checked((int)reader.TokenStartIndex) + offset;
                    var name =
                        reader.GetString()
                        ?? throw new JsonException("A JSON property name cannot be null.");
                    if (!reader.Read())
                    {
                        throw new JsonException("The JSON property has no value.");
                    }

                    value.Properties!.Add(
                        new JsoncPropertyNode(
                            name,
                            nameStart,
                            ReadValue(ref reader, source, offset)
                        )
                    );
                }

                throw new JsonException("The JSON object is not closed.");
            }
            case JsonTokenType.StartArray:
            {
                var value = new JsoncValueNode(JsonValueKind.Array, start)
                {
                    OpenEnd = checked((int)reader.BytesConsumed) + offset,
                };
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        value.CloseStart = checked((int)reader.TokenStartIndex) + offset;
                        value.End = checked((int)reader.BytesConsumed) + offset;
                        SetCommas(value, source);
                        return value;
                    }

                    value.Items!.Add(ReadValue(ref reader, source, offset));
                }

                throw new JsonException("The JSON array is not closed.");
            }
            case JsonTokenType.String:
                return new JsoncValueNode(
                    JsonValueKind.String,
                    start,
                    checked((int)reader.BytesConsumed) + offset
                );
            case JsonTokenType.Number:
                return new JsoncValueNode(
                    JsonValueKind.Number,
                    start,
                    checked((int)reader.BytesConsumed) + offset
                );
            case JsonTokenType.True:
                return new JsoncValueNode(
                    JsonValueKind.True,
                    start,
                    checked((int)reader.BytesConsumed) + offset
                );
            case JsonTokenType.False:
                return new JsoncValueNode(
                    JsonValueKind.False,
                    start,
                    checked((int)reader.BytesConsumed) + offset
                );
            case JsonTokenType.Null:
                return new JsoncValueNode(
                    JsonValueKind.Null,
                    start,
                    checked((int)reader.BytesConsumed) + offset
                );
            default:
                throw new JsonException($"Unexpected JSON token {reader.TokenType}.");
        }
    }

    private static void SetCommas(JsoncValueNode value, byte[] source)
    {
        var count = value.Properties?.Count ?? value.Items!.Count;
        var names =
            value.Properties is not null && count > 1
                ? new HashSet<string>(StringComparer.Ordinal)
                : null;
        for (var index = 0; index < count; index++)
        {
            if (
                value.Properties is not null
                && names is not null
                && !names.Add(value.Properties[index].Name)
            )
            {
                throw new JsonException(
                    $"The JSON object contains duplicate property '{value.Properties[index].Name}'."
                );
            }

            var itemEnd = value.Properties is null
                ? value.Items![index].End
                : value.Properties[index].Value.End;
            int nextStart;
            if (index + 1 >= count)
            {
                nextStart = value.CloseStart;
            }
            else if (value.Properties is null)
            {
                nextStart = value.Items![index + 1].Start;
            }
            else
            {
                nextStart = value.Properties[index + 1].NameStart;
            }

            var comma = FindComma(source, itemEnd, nextStart);
            if (value.Properties is null)
            {
                value.Items![index].CommaAfter = comma;
            }
            else
            {
                value.Properties[index].CommaAfter = comma;
            }
        }
    }

    internal static int FindComma(byte[] source, int start, int end)
    {
        var index = start;
        while (index < end)
        {
            if (IsWhiteSpace(source[index]))
            {
                index++;
                continue;
            }

            if (source[index] == (byte)'/' && index + 1 < end)
            {
                if (source[index + 1] == (byte)'/')
                {
                    index += 2;
                    while (index < end && source[index] is not (byte)'\r' and not (byte)'\n')
                    {
                        index++;
                    }

                    continue;
                }

                if (source[index + 1] == (byte)'*')
                {
                    index += 2;
                    while (
                        index + 1 < end
                        && (source[index] != (byte)'*' || source[index + 1] != (byte)'/')
                    )
                    {
                        index++;
                    }

                    index = Math.Min(index + 2, end);
                    continue;
                }
            }

            return source[index] == (byte)',' ? index : -1;
        }

        return -1;
    }

    private static bool IsWhiteSpace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}

internal sealed class JsoncValueNode
{
    internal JsoncValueNode(JsonValueKind kind, int start, int end = 0)
    {
        Kind = kind;
        Start = start;
        End = end;
        if (kind == JsonValueKind.Object)
        {
            Properties = [];
        }
        else if (kind == JsonValueKind.Array)
        {
            Items = [];
        }
    }

    internal JsonValueKind Kind { get; }

    internal int Start { get; }

    internal int End { get; set; }

    internal int OpenEnd { get; set; }

    internal int CloseStart { get; set; }

    internal List<JsoncPropertyNode>? Properties { get; }

    internal List<JsoncValueNode>? Items { get; }

    internal int? CommaAfter { get; set; }
}

internal sealed class JsoncPropertyNode(string name, int nameStart, JsoncValueNode value)
{
    internal string Name { get; } = name;

    internal int NameStart { get; } = nameStart;

    internal JsoncValueNode Value { get; } = value;

    internal int? CommaAfter { get; set; }
}
