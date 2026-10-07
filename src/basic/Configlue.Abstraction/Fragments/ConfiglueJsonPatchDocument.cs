using System.ComponentModel;
using System.Text.Json;

namespace Configlue;

/// <summary>A parsed RFC 6902 document.</summary>
/// <remarks>
/// Product-neutral document parsing mirrored from
/// <c>src/fragments:src/SparseFragments/JsonPatch/JsonPatchDocument.cs</c>
/// (namespace <c>SparseFragments</c>). The Configlue adapter surface is the
/// public type plus its two <c>Parse</c> overloads; operation validation lives
/// in <see cref="JsonPatchOperation"/>.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class JsonPatchDocument
{
    private readonly IReadOnlyList<JsonPatchOperation> _operations;

    internal JsonPatchDocument(IReadOnlyList<JsonPatchOperation> operations)
    {
        _operations = operations;
    }

    /// <summary>The operations in document order.</summary>
    public IReadOnlyList<JsonPatchOperation> Operations => _operations;

    /// <summary>Whether the document has no operations.</summary>
    public bool IsEmpty => _operations.Count == 0;

    /// <summary>Parses UTF-8 JSON Patch bytes.</summary>
    /// <exception cref="JsonPatchException">When the document is malformed.</exception>
    public static JsonPatchDocument Parse(ReadOnlyMemory<byte> utf8)
    {
        JsonDocument doc;
        try
        {
            if (utf8.IsEmpty)
            {
                throw new JsonPatchException(
                    JsonPatchErrorKind.MalformedDocument,
                    "JSON Patch document is empty."
                );
            }

            doc = JsonDocument.Parse(utf8);
        }
        catch (JsonPatchException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedDocument,
                "JSON Patch document is not valid JSON.",
                ex
            );
        }

        using (doc)
        {
            return Parse(doc.RootElement);
        }
    }

    /// <summary>Parses a JSON string patch document.</summary>
    public static JsonPatchDocument Parse(string json)
    {
        if (json is null)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedDocument,
                "JSON Patch document is null."
            );
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedDocument,
                "JSON Patch document is not valid JSON.",
                ex
            );
        }

        using (doc)
        {
            return Parse(doc.RootElement);
        }
    }

    internal static JsonPatchDocument Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedDocument,
                "JSON Patch document must be an array."
            );
        }

        var count = root.GetArrayLength();
        var operations =
            count == 0 ? Array.Empty<JsonPatchOperation>() : new JsonPatchOperation[count];
        var index = 0;
        foreach (var element in root.EnumerateArray())
        {
            operations[index++] = JsonPatchOperation.Parse(element);
        }

        return new JsonPatchDocument(operations);
    }
}
