using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Configlue;

/// <summary>One RFC 6902 operation.</summary>
/// <remarks>
/// Product-neutral RFC 6902 parsing/validation core mirrored from
/// <c>src/fragments:src/SparseFragments/JsonPatch/JsonPatchOperation.cs</c>
/// (namespace <c>SparseFragments</c>). Configlue keeps its own public contract
/// (namespace <c>Configlue</c>) with no runtime dependency on SparseFragments:
/// only the <see cref="Op"/>, <see cref="Path"/>, <see cref="From"/>,
/// <see cref="Value"/>, and <see cref="HasValue"/> members are public, while the
/// pre-parsed pointer tokens stay internal so the engine never re-tokenizes.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class JsonPatchOperation
{
    /// <summary>Initializes a new instance.</summary>
    public JsonPatchOperation(string op, string path, string? from, JsonNode? value, bool hasValue)
        : this(
            op,
            path,
            JsonPointer.Parse(path),
            from,
            from is null ? null : JsonPointer.Parse(from),
            value,
            hasValue
        ) { }

    /// <summary>Initializes a new instance with pre-parsed pointer tokens.</summary>
    internal JsonPatchOperation(
        string op,
        string path,
        string[] pathTokens,
        string? from,
        string[]? fromTokens,
        JsonNode? value,
        bool hasValue
    )
    {
        Op = op;
        Path = path;
        PathTokens = pathTokens;
        From = from;
        FromTokens = fromTokens;
        Value = value;
        HasValue = hasValue;
    }

    /// <summary>The operation name.</summary>
    public string Op { get; }

    /// <summary>The target pointer.</summary>
    public string Path { get; }

    /// <summary>The parsed target pointer tokens, reused during apply.</summary>
    internal string[] PathTokens { get; }

    /// <summary>The source pointer for move/copy.</summary>
    public string? From { get; }

    /// <summary>The parsed source pointer tokens for move/copy.</summary>
    internal string[]? FromTokens { get; }

    /// <summary>The operation value for add/replace/test.</summary>
    public JsonNode? Value { get; }

    /// <summary>Whether a value was supplied (distinguishes explicit JSON null).</summary>
    public bool HasValue { get; }

    internal static JsonPatchOperation Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedDocument,
                "Each JSON Patch operation must be an object."
            );
        }

        if (
            !element.TryGetProperty("op", out var opElement)
            || opElement.ValueKind != JsonValueKind.String
        )
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedDocument,
                "Each JSON Patch operation must have a string 'op'."
            );
        }

        if (
            !element.TryGetProperty("path", out var pathElement)
            || pathElement.ValueKind != JsonValueKind.String
        )
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedDocument,
                "Each JSON Patch operation must have a string 'path'."
            );
        }

        var op = opElement.GetString()!;
        var path = pathElement.GetString()!;
        // Validate pointer syntax eagerly so malformed pointers surface distinctly,
        // keeping the parsed tokens so apply does not tokenize them again.
        var pathTokens = JsonPointer.Parse(path);

        string? from = null;
        string[]? fromTokens = null;
        var hasFrom = false;
        if (element.TryGetProperty("from", out var fromElement))
        {
            hasFrom = true;
            if (fromElement.ValueKind != JsonValueKind.String)
            {
                throw new JsonPatchException(
                    JsonPatchErrorKind.MalformedDocument,
                    "A JSON Patch 'from' must be a string."
                );
            }

            from = fromElement.GetString()!;
            fromTokens = JsonPointer.Parse(from);
        }

        var hasValue = element.TryGetProperty("value", out var valueElement);
        JsonNode? value = null;
        if (hasValue)
        {
            // Detach from the parsed document without converting the value to text and reparsing it.
            value = valueElement.ValueKind switch
            {
                JsonValueKind.Object => JsonObject.Create(valueElement.Clone()),
                JsonValueKind.Array => JsonArray.Create(valueElement.Clone()),
                JsonValueKind.Null => null,
                _ => JsonValue.Create(valueElement.Clone()),
            };
        }

        switch (op)
        {
            case "add":
            case "replace":
            case "test":
                if (!hasValue)
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.MalformedDocument,
                        $"JSON Patch operation '{op}' requires a 'value'."
                    );
                }

                break;
            case "remove":
                break;
            case "move":
            case "copy":
                if (!hasFrom || from is null)
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.MalformedDocument,
                        $"JSON Patch operation '{op}' requires a 'from'."
                    );
                }

                break;
            default:
                throw new JsonPatchException(
                    JsonPatchErrorKind.UnknownOperation,
                    $"Unknown JSON Patch operation '{op}'."
                );
        }

        return new JsonPatchOperation(op, path, pathTokens, from, fromTokens, value, hasValue);
    }
}
