using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

#if CONFIGLUE_FRAGMENT_RUNTIME
namespace Configlue;

#else
namespace SparseFragments;

#endif

/// <summary>Applies RFC 6902 operations to a JSON document atomically.</summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public static class JsonPatchEngine
{
    /// <summary>The result of applying a patch document.</summary>
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public sealed class ApplyResult
    {
        internal ApplyResult(JsonNode? node, bool isAbsent)
        {
            Node = node;
            IsAbsent = isAbsent;
        }

        /// <summary>The resulting document (null for JSON null).</summary>
        public JsonNode? Node { get; }

        /// <summary>Whether the root was removed.</summary>
        public bool IsAbsent { get; }
    }

    /// <summary>Applies all operations in order; throws without partial effects on failure.</summary>
    /// <param name="baseline">The baseline document (null for JSON null).</param>
    /// <param name="baselineIsAbsent">Whether there is no baseline document.</param>
    /// <param name="document">The patch document to apply.</param>
    /// <param name="propertyNameComparison">How object property names compare (mirrors <c>JsonSerializerOptions.PropertyNameCaseInsensitive</c>).</param>
    /// <exception cref="JsonPatchException">On any RFC 6902 failure.</exception>
    public static ApplyResult Apply(
        JsonNode? baseline,
        bool baselineIsAbsent,
        JsonPatchDocument document,
        StringComparison propertyNameComparison = StringComparison.Ordinal
    )
    {
        if (document is null)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedDocument,
                "JSON Patch document is null."
            );
        }

        if (
            propertyNameComparison != StringComparison.Ordinal
            && propertyNameComparison != StringComparison.OrdinalIgnoreCase
        )
        {
            throw new ArgumentOutOfRangeException(nameof(propertyNameComparison));
        }

        JsonNode? current = baselineIsAbsent ? null : Clone(baseline);
        var isAbsent = baselineIsAbsent;

        foreach (var operation in document.Operations)
        {
            ApplyOne(ref current, ref isAbsent, operation, propertyNameComparison);
        }

        return new ApplyResult(current, isAbsent);
    }

    /// <summary>Creates a semantically equivalent patch document diffing two JSON states.</summary>
    /// <remarks>Objects diff recursively; arrays and scalars collapse to whole-value replace.</remarks>
    public static JsonPatchDocument Diff(
        JsonNode? before,
        bool beforeIsAbsent,
        JsonNode? after,
        bool afterIsAbsent
    )
    {
        var ops = new List<JsonPatchOperation>();
        if (beforeIsAbsent && afterIsAbsent)
        {
            return new JsonPatchDocument(ops);
        }

        if (beforeIsAbsent)
        {
            ops.Add(new JsonPatchOperation("add", string.Empty, null, Clone(after), true));
            return new JsonPatchDocument(ops);
        }

        if (afterIsAbsent)
        {
            ops.Add(new JsonPatchOperation("remove", string.Empty, null, null, false));
            return new JsonPatchDocument(ops);
        }

        DiffNodes(before, after, string.Empty, ops);
        return new JsonPatchDocument(ops);
    }

    /// <summary>Serializes a patch document to UTF-8 bytes.</summary>
    public static byte[] Serialize(
        JsonPatchDocument document,
        JsonSerializerOptions? options = null
    )
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var operation in document.Operations)
            {
                writer.WriteStartObject();
                writer.WriteString("op", operation.Op);
                writer.WriteString("path", operation.Path);
                if (operation.Op is "move" or "copy")
                {
                    writer.WriteString("from", operation.From);
                }

                if (operation.HasValue)
                {
                    writer.WritePropertyName("value");
                    if (operation.Value is null)
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        operation.Value.WriteTo(writer, options);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return stream.ToArray();
    }

    internal static JsonNode? Clone(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return JsonNode.Parse(node.ToJsonString());
    }

#pragma warning disable S1075 // RFC 6901 JSON Pointer uses slash delimiters.
    private static void DiffNodes(
        JsonNode? before,
        JsonNode? after,
        string path,
        List<JsonPatchOperation> ops
    )
    {
        if (JsonNode.DeepEquals(before, after))
        {
            return;
        }

        if (before is JsonObject beforeObject && after is JsonObject afterObject)
        {
            foreach (var key in ((IDictionary<string, JsonNode?>)beforeObject).Keys)
            {
                if (afterObject.ContainsKey(key))
                {
                    continue;
                }

                ops.Add(
                    new JsonPatchOperation(
                        "remove",
                        path + "/" + JsonPointer.Escape(key),
                        null,
                        null,
                        false
                    )
                );
            }

            foreach (var property in afterObject)
            {
                if (!beforeObject.TryGetPropertyValue(property.Key, out var beforeValue))
                {
                    ops.Add(
                        new JsonPatchOperation(
                            "add",
                            path + "/" + JsonPointer.Escape(property.Key),
                            null,
                            Clone(property.Value),
                            true
                        )
                    );
                }
                else
                {
                    DiffNodes(
                        beforeValue,
                        property.Value,
                        path + "/" + JsonPointer.Escape(property.Key),
                        ops
                    );
                }
            }

            return;
        }

        ops.Add(new JsonPatchOperation("replace", path, null, Clone(after), true));
    }

#pragma warning restore S1075

    private static void ApplyOne(
        ref JsonNode? current,
        ref bool isAbsent,
        JsonPatchOperation operation,
        StringComparison propertyNameComparison
    )
    {
        switch (operation.Op)
        {
            case "add":
                ApplyAdd(
                    ref current,
                    ref isAbsent,
                    operation.Path,
                    Clone(operation.Value),
                    operation.HasValue,
                    propertyNameComparison
                );
                break;
            case "remove":
                ApplyRemove(ref current, ref isAbsent, operation.Path, propertyNameComparison);
                break;
            case "replace":
                ApplyReplace(
                    ref current,
                    ref isAbsent,
                    operation.Path,
                    Clone(operation.Value),
                    propertyNameComparison
                );
                break;
            case "move":
                ApplyMove(
                    ref current,
                    ref isAbsent,
                    operation.From!,
                    operation.Path,
                    propertyNameComparison
                );
                break;
            case "copy":
                ApplyCopy(
                    ref current,
                    ref isAbsent,
                    operation.From!,
                    operation.Path,
                    propertyNameComparison
                );
                break;
            case "test":
                ApplyTest(
                    current,
                    isAbsent,
                    operation.Path,
                    operation.Value,
                    propertyNameComparison
                );
                break;
            default:
                throw new JsonPatchException(
                    JsonPatchErrorKind.UnknownOperation,
                    $"Unknown JSON Patch operation '{operation.Op}'."
                );
        }
    }

    private static void ApplyAdd(
        ref JsonNode? current,
        ref bool isAbsent,
        string path,
        JsonNode? value,
        bool hasValue,
        StringComparison propertyNameComparison
    )
    {
        if (!hasValue)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedDocument,
                "JSON Patch 'add' requires a 'value'."
            );
        }

        if (path.Length == 0)
        {
            current = value;
            isAbsent = false;
            return;
        }

        if (isAbsent || current is null)
        {
            // A JSON null root has no object/array parent to add into.
            if (isAbsent)
            {
                throw new JsonPatchException(
                    JsonPatchErrorKind.MissingParent,
                    $"Cannot add '{path}' because its parent does not exist."
                );
            }

            throw new JsonPatchException(
                JsonPatchErrorKind.MissingParent,
                $"Cannot add '{path}' because its parent does not exist."
            );
        }

        var tokens = JsonPointer.Parse(path);
        var parent = ResolveParent(current, tokens, isAdd: true, path, propertyNameComparison);
        SetChild(parent, tokens[tokens.Length - 1], value, path, propertyNameComparison);
    }

    private static void ApplyRemove(
        ref JsonNode? current,
        ref bool isAbsent,
        string path,
        StringComparison propertyNameComparison
    )
    {
        if (path.Length == 0)
        {
            if (isAbsent)
            {
                throw new JsonPatchException(
                    JsonPatchErrorKind.MissingTarget,
                    "Cannot remove the document root because it does not exist."
                );
            }

            current = null;
            isAbsent = true;
            return;
        }

        if (isAbsent || current is null)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MissingTarget,
                $"Cannot remove '{path}' because its target does not exist."
            );
        }

        var tokens = JsonPointer.Parse(path);
        var parent = ResolveParent(current, tokens, isAdd: false, path, propertyNameComparison);
        RemoveChild(parent, tokens[tokens.Length - 1], path, propertyNameComparison);
    }

    private static void ApplyReplace(
        ref JsonNode? current,
        ref bool isAbsent,
        string path,
        JsonNode? value,
        StringComparison propertyNameComparison
    )
    {
        if (path.Length == 0)
        {
            if (isAbsent)
            {
                throw new JsonPatchException(
                    JsonPatchErrorKind.MissingTarget,
                    "Cannot replace the document root because it does not exist."
                );
            }

            current = value;
            return;
        }

        if (isAbsent || current is null)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MissingTarget,
                $"Cannot replace '{path}' because its target does not exist."
            );
        }

        var tokens = JsonPointer.Parse(path);
        var parent = ResolveParent(current, tokens, isAdd: false, path, propertyNameComparison);
        ReplaceChild(parent, tokens[tokens.Length - 1], value, path, propertyNameComparison);
    }

    private static void ApplyMove(
        ref JsonNode? current,
        ref bool isAbsent,
        string from,
        string path,
        StringComparison propertyNameComparison
    )
    {
        var value = ReadValue(current, isAbsent, from, propertyNameComparison);
        // Remove first so array indices shift per RFC semantics.
        ApplyRemove(ref current, ref isAbsent, from, propertyNameComparison);
        try
        {
            ApplyAdd(
                ref current,
                ref isAbsent,
                path,
                value,
                hasValue: true,
                propertyNameComparison
            );
        }
        catch (JsonPatchException ex) when (ex.Kind == JsonPatchErrorKind.MissingParent)
        {
            throw new JsonPatchException(JsonPatchErrorKind.MissingParent, ex.Message, ex);
        }
    }

    private static void ApplyCopy(
        ref JsonNode? current,
        ref bool isAbsent,
        string from,
        string path,
        StringComparison propertyNameComparison
    )
    {
        var value = ReadValue(current, isAbsent, from, propertyNameComparison);
        ApplyAdd(
            ref current,
            ref isAbsent,
            path,
            Clone(value),
            hasValue: true,
            propertyNameComparison
        );
    }

    private static void ApplyTest(
        JsonNode? current,
        bool isAbsent,
        string path,
        JsonNode? expected,
        StringComparison propertyNameComparison
    )
    {
        var actual = ReadValue(current, isAbsent, path, propertyNameComparison);
        if (!JsonNode.DeepEquals(actual, expected))
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.TestFailed,
                $"JSON Patch test failed for '{path}'."
            );
        }
    }

    private static JsonNode? ReadValue(
        JsonNode? current,
        bool isAbsent,
        string path,
        StringComparison propertyNameComparison
    )
    {
        if (path.Length == 0)
        {
            if (isAbsent)
            {
                throw new JsonPatchException(
                    JsonPatchErrorKind.MissingTarget,
                    "The document root does not exist."
                );
            }

            return Clone(current);
        }

        if (isAbsent || current is null)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MissingTarget,
                $"Cannot read '{path}' because its target does not exist."
            );
        }

        var tokens = JsonPointer.Parse(path);
        JsonNode? node = current;
        foreach (var token in tokens)
        {
            node = GetChild(node, token, path, isAdd: false, propertyNameComparison);
        }

        return Clone(node);
    }

    private static JsonNode ResolveParent(
        JsonNode root,
        string[] tokens,
        bool isAdd,
        string path,
        StringComparison propertyNameComparison
    )
    {
        JsonNode node = root;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            node =
                GetChild(node, tokens[i], path, isAdd, propertyNameComparison)
                ?? throw new JsonPatchException(
                    isAdd ? JsonPatchErrorKind.MissingParent : JsonPatchErrorKind.MissingTarget,
                    isAdd
                        ? $"Cannot add '{path}' because its parent does not exist."
                        : $"Cannot resolve '{path}' because its target does not exist."
                );
        }

        return node;
    }

    /// <summary>Resolves an existing property key honoring the configured comparison.</summary>
    private static bool TryResolveKey(
        JsonObject obj,
        string token,
        StringComparison comparison,
        out string actualKey
    )
    {
        if (obj.TryGetPropertyValue(token, out _))
        {
            actualKey = token;
            return true;
        }

        if (comparison == StringComparison.OrdinalIgnoreCase)
        {
            foreach (var key in ((IDictionary<string, JsonNode?>)obj).Keys)
            {
                if (!string.Equals(key, token, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                actualKey = key;
                return true;
            }
        }

        actualKey = token;
        return false;
    }

    private static JsonNode? GetChild(
        JsonNode? node,
        string token,
        string path,
        bool isAdd,
        StringComparison propertyNameComparison
    )
    {
        switch (node)
        {
            case JsonObject obj:
                if (
                    TryResolveKey(obj, token, propertyNameComparison, out var key)
                    && obj.TryGetPropertyValue(key, out var value)
                )
                {
                    // Explicit JSON null is a present value; only a missing key is absent.
                    // A present null cannot be traversed further.
                    return value;
                }

                throw new JsonPatchException(
                    isAdd ? JsonPatchErrorKind.MissingParent : JsonPatchErrorKind.MissingTarget,
                    isAdd
                        ? $"Cannot add '{path}' because its parent does not exist."
                        : $"Cannot resolve '{path}' because its target does not exist."
                );
            case JsonArray array:
                if (token == "-")
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.InvalidArrayIndex,
                        $"Cannot resolve '{path}' because '-' is only valid for append."
                    );
                }

                var index = ParseIndex(token, path);
                if (index < 0 || index >= array.Count)
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.InvalidArrayIndex,
                        $"Array index in '{path}' is out of range."
                    );
                }

                return array[index];
            default:
                throw new JsonPatchException(
                    isAdd ? JsonPatchErrorKind.MissingParent : JsonPatchErrorKind.MissingTarget,
                    isAdd
                        ? $"Cannot add '{path}' because its parent does not exist."
                        : $"Cannot resolve '{path}' because its target does not exist."
                );
        }
    }

    private static void SetChild(
        JsonNode parent,
        string token,
        JsonNode? value,
        string path,
        StringComparison propertyNameComparison
    )
    {
        switch (parent)
        {
            case JsonObject obj:
                // Preserve the canonical key when matching case-insensitively.
                obj[TryResolveKey(obj, token, propertyNameComparison, out var key) ? key : token] =
                    value;
                break;
            case JsonArray array:
                if (token == "-")
                {
                    array.Add(value);
                    break;
                }

                var index = ParseIndex(token, path);
                if (index < 0 || index > array.Count)
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.InvalidArrayIndex,
                        $"Array index in '{path}' is out of range."
                    );
                }

                if (index == array.Count)
                {
                    array.Add(value);
                }
                else
                {
                    array.Insert(index, value);
                }

                break;
            default:
                throw new JsonPatchException(
                    JsonPatchErrorKind.MissingParent,
                    $"Cannot add '{path}' because its parent does not exist."
                );
        }
    }

    private static void RemoveChild(
        JsonNode parent,
        string token,
        string path,
        StringComparison propertyNameComparison
    )
    {
        switch (parent)
        {
            case JsonObject obj:
                if (
                    TryResolveKey(obj, token, propertyNameComparison, out var removeKey)
                    && obj.Remove(removeKey)
                )
                {
                    break;
                }

                throw new JsonPatchException(
                    JsonPatchErrorKind.MissingTarget,
                    $"Cannot remove '{path}' because its target does not exist."
                );
            case JsonArray array:
                if (token == "-")
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.InvalidArrayIndex,
                        $"Cannot remove '{path}' because '-' is not a valid index."
                    );
                }

                var index = ParseIndex(token, path);
                if (index < 0 || index >= array.Count)
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.InvalidArrayIndex,
                        $"Array index in '{path}' is out of range."
                    );
                }

                array.RemoveAt(index);
                break;
            default:
                throw new JsonPatchException(
                    JsonPatchErrorKind.MissingTarget,
                    $"Cannot remove '{path}' because its target does not exist."
                );
        }
    }

    private static void ReplaceChild(
        JsonNode parent,
        string token,
        JsonNode? value,
        string path,
        StringComparison propertyNameComparison
    )
    {
        switch (parent)
        {
            case JsonObject obj:
                if (!TryResolveKey(obj, token, propertyNameComparison, out var replaceKey))
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.MissingTarget,
                        $"Cannot replace '{path}' because its target does not exist."
                    );
                }

                obj[replaceKey] = value;
                break;
            case JsonArray array:
                if (token == "-")
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.InvalidArrayIndex,
                        $"Cannot replace '{path}' because '-' is not a valid index."
                    );
                }

                var index = ParseIndex(token, path);
                if (index < 0 || index >= array.Count)
                {
                    throw new JsonPatchException(
                        JsonPatchErrorKind.InvalidArrayIndex,
                        $"Array index in '{path}' is out of range."
                    );
                }

                array[index] = value;
                break;
            default:
                throw new JsonPatchException(
                    JsonPatchErrorKind.MissingTarget,
                    $"Cannot replace '{path}' because its target does not exist."
                );
        }
    }

    private static int ParseIndex(string token, string path)
    {
        if (token.Length == 0)
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.InvalidArrayIndex,
                $"Array index in '{path}' is invalid."
            );
        }

        // RFC 6902 array indices are base-10 without leading zeros (except "0" itself).
        if ((token[0] < '0' || token[0] > '9') || (token.Length > 1 && token[0] == '0'))
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.InvalidArrayIndex,
                $"Array index in '{path}' is invalid."
            );
        }

        for (var position = 0; position < token.Length; position++)
        {
            var c = token[position];
            if (c < '0' || c > '9')
            {
                throw new JsonPatchException(
                    JsonPatchErrorKind.InvalidArrayIndex,
                    $"Array index in '{path}' is invalid."
                );
            }
        }

        if (!int.TryParse(token, out var index))
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.InvalidArrayIndex,
                $"Array index in '{path}' is invalid."
            );
        }

        return index;
    }
}
