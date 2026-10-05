using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

#if CONFIGLUE_FRAGMENT_RUNTIME
namespace Configlue;

#else
namespace SparseFragments;

#endif

/// <summary>One RFC 6902 operation.</summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class JsonPatchOperation
{
    /// <summary>Initializes a new instance.</summary>
    public JsonPatchOperation(string op, string path, string? from, JsonNode? value, bool hasValue)
    {
        Op = op;
        Path = path;
        From = from;
        Value = value;
        HasValue = hasValue;
    }

    /// <summary>The operation name.</summary>
    public string Op { get; }

    /// <summary>The target pointer.</summary>
    public string Path { get; }

    /// <summary>The source pointer for move/copy.</summary>
    public string? From { get; }

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
        // Validate pointer syntax eagerly so malformed pointers surface distinctly.
        JsonPointer.Parse(path);

        string? from = null;
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
            JsonPointer.Parse(from);
        }

        var hasValue = element.TryGetProperty("value", out var valueElement);
        JsonNode? value = null;
        if (hasValue)
        {
            value = JsonNode.Parse(valueElement.GetRawText());
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

        return new JsonPatchOperation(op, path, from, value, hasValue);
    }
}
