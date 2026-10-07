using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Configlue;

/// <summary>Applies RFC 6902 operations to a JSON document atomically.</summary>
/// <remarks>
/// Product-neutral RFC 6902 mechanics (parse, JSON Pointer handling, validation,
/// patch application/export) mirrored from
/// <c>src/fragments:src/SparseFragments/JsonPatch/JsonPatchEngine.cs</c>
/// (namespace <c>SparseFragments</c>). The Configlue adapter surface is the
/// public type plus the <c>ConfiglueJsonPatch</c> facade; the engine itself takes
/// only JSON DOM inputs so a future shared-source cutover keeps behavior while
/// swapping this copy for the upstream neutral implementation. There is no
/// runtime dependency on SparseFragments.
/// </remarks>
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

        if (document.Operations.Count == 0)
        {
            // Return independent bytes because callers can mutate the result.
            return new byte[] { (byte)'[', (byte)']' };
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

        return node.DeepClone();
    }

#pragma warning disable S1075 // RFC 6901 JSON Pointer uses slash delimiters.
    private static void DiffNodes(
        JsonNode? before,
        JsonNode? after,
        string path,
        List<JsonPatchOperation> ops
    )
    {
        if (RfcJsonEquality.AreEqual(before, after))
        {
            return;
        }

        DiffUnequalNodes(before, after, path, ops);
    }

    // Call only after comparing the nodes so unchanged members never need a
    // path allocation, and changed members are not compared a second time.
    private static void DiffUnequalNodes(
        JsonNode? before,
        JsonNode? after,
        string path,
        List<JsonPatchOperation> ops
    )
    {
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
                else if (!RfcJsonEquality.AreEqual(beforeValue, property.Value))
                {
                    DiffUnequalNodes(
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
                    operation.PathTokens,
                    Clone(operation.Value),
                    operation.HasValue,
                    propertyNameComparison
                );
                break;
            case "remove":
                ApplyRemove(
                    ref current,
                    ref isAbsent,
                    operation.Path,
                    operation.PathTokens,
                    propertyNameComparison
                );
                break;
            case "replace":
                ApplyReplace(
                    ref current,
                    ref isAbsent,
                    operation.Path,
                    operation.PathTokens,
                    Clone(operation.Value),
                    propertyNameComparison
                );
                break;
            case "move":
                ApplyMove(
                    ref current,
                    ref isAbsent,
                    operation.From!,
                    operation.FromTokens!,
                    operation.Path,
                    operation.PathTokens,
                    propertyNameComparison
                );
                break;
            case "copy":
                ApplyCopy(
                    ref current,
                    ref isAbsent,
                    operation.From!,
                    operation.FromTokens!,
                    operation.Path,
                    operation.PathTokens,
                    propertyNameComparison
                );
                break;
            case "test":
                ApplyTest(
                    current,
                    isAbsent,
                    operation.Path,
                    operation.PathTokens,
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
        string[] tokens,
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

        var parent = ResolveParent(current, tokens, isAdd: true, path, propertyNameComparison);
        SetChild(parent, tokens[tokens.Length - 1], value, path, propertyNameComparison);
    }

    private static void ApplyRemove(
        ref JsonNode? current,
        ref bool isAbsent,
        string path,
        string[] tokens,
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

        var parent = ResolveParent(current, tokens, isAdd: false, path, propertyNameComparison);
        RemoveChild(parent, tokens[tokens.Length - 1], path, propertyNameComparison);
    }

    private static void ApplyReplace(
        ref JsonNode? current,
        ref bool isAbsent,
        string path,
        string[] tokens,
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

        var parent = ResolveParent(current, tokens, isAdd: false, path, propertyNameComparison);
        ReplaceChild(parent, tokens[tokens.Length - 1], value, path, propertyNameComparison);
    }

    private static void ApplyMove(
        ref JsonNode? current,
        ref bool isAbsent,
        string from,
        string[] fromTokens,
        string path,
        string[] pathTokens,
        StringComparison propertyNameComparison
    )
    {
        // RFC 6902 section 4.6: the 'from' location MUST NOT be a proper prefix of 'path'.
        if (
            fromTokens.Length < pathTokens.Length
            && (fromTokens.Length == 0 || IsTokenPrefix(fromTokens, pathTokens))
        )
        {
            throw new JsonPatchException(
                JsonPatchErrorKind.MalformedPointer,
                $"JSON Patch move 'from' location '{from}' must not be a proper prefix of '{path}'."
            );
        }

        var value = ReadValue(current, isAbsent, from, fromTokens, propertyNameComparison);
        // Remove first so array indices shift per RFC semantics.
        ApplyRemove(ref current, ref isAbsent, from, fromTokens, propertyNameComparison);
        try
        {
            ApplyAdd(
                ref current,
                ref isAbsent,
                path,
                pathTokens,
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
        string[] fromTokens,
        string path,
        string[] pathTokens,
        StringComparison propertyNameComparison
    )
    {
        var value = ReadValue(current, isAbsent, from, fromTokens, propertyNameComparison);
        ApplyAdd(
            ref current,
            ref isAbsent,
            path,
            pathTokens,
            Clone(value),
            hasValue: true,
            propertyNameComparison
        );
    }

    private static void ApplyTest(
        JsonNode? current,
        bool isAbsent,
        string path,
        string[] tokens,
        JsonNode? expected,
        StringComparison propertyNameComparison
    )
    {
        var actual = ReadValue(current, isAbsent, path, tokens, propertyNameComparison);
        if (!RfcJsonEquality.AreEqual(actual, expected))
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
        string[] pathTokens,
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

        JsonNode? node = current;
        foreach (var token in pathTokens)
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

    private static bool IsTokenPrefix(string[] prefix, string[] tokens)
    {
        for (var index = 0; index < prefix.Length; index++)
        {
            if (!string.Equals(prefix[index], tokens[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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

/// <summary>RFC 6902 structural JSON equality used by the <c>test</c> operation.</summary>
/// <remarks>
/// Product-neutral core mirrored from
/// <c>src/fragments:src/SparseFragments/JsonPatch/RfcJsonEquality.cs</c>
/// (namespace <c>SparseFragments</c>, also internal there). RFC 6902 §4.6 defines
/// equality structurally: strings by exact value, numbers by numerical equality
/// (so <c>1</c>, <c>1.0</c> and <c>10e-1</c> are equal), arrays by length/order
/// with recursive equality, objects by identical member sets with recursively
/// equal values regardless of member order. This deliberately does not rely on
/// <see cref="JsonNode.DeepEquals(JsonNode?, JsonNode?)"/> lexical behavior.
/// </remarks>
internal static class RfcJsonEquality
{
    public static bool AreEqual(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left is JsonValue leftValue && right is JsonValue rightValue)
        {
            return JsonValuesEqual(leftValue, rightValue);
        }

        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            if (leftArray.Count != rightArray.Count)
            {
                return false;
            }

            for (var index = 0; index < leftArray.Count; index++)
            {
                if (!AreEqual(leftArray[index], rightArray[index]))
                {
                    return false;
                }
            }

            return true;
        }

        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            if (leftObject.Count != rightObject.Count)
            {
                return false;
            }

            foreach (var property in leftObject)
            {
                if (
                    !rightObject.TryGetPropertyValue(property.Key, out var current)
                    || !AreEqual(property.Value, current)
                )
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S2589",
        Justification = "Second TryGetValue runs only when decimal conversion fails on at least one operand."
    )]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Bug",
        "S1244",
        Justification = "RFC 6902 requires exact numerical equality; decimal covers representable values, double fallback covers the rest."
    )]
    private static bool JsonValuesEqual(JsonValue left, JsonValue right)
    {
        if (left.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            // JSON numbers with different lexical forms share the same value kind.
            if (
                left.TryGetValue<decimal>(out var leftDecimal)
                && right.TryGetValue<decimal>(out var rightDecimal)
            )
            {
                return leftDecimal == rightDecimal;
            }

            if (
                left.TryGetValue<double>(out var leftDouble)
                && right.TryGetValue<double>(out var rightDouble)
            )
            {
                return leftDouble.Equals(rightDouble);
            }

            return false;
        }

        if (left.GetValueKind() != right.GetValueKind())
        {
            return false;
        }

        return left.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => true,
            System.Text.Json.JsonValueKind.Null => true,
            System.Text.Json.JsonValueKind.String => left.GetValue<string>()
                == right.GetValue<string>(),
            _ => JsonNode.DeepEquals(left, right),
        };
    }
}
