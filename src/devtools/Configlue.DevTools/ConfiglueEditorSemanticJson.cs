using System.Text.Json;
using System.Text.Json.Nodes;

namespace Configlue.DevTools;

/// <summary>
/// Schema-aware JSON helpers for DevTools semantic editing.
/// </summary>
/// <remarks>
/// <para>
/// Internal to the DevTools package. The Monaco document is an effective-state
/// projection, never a storage document: these helpers compare drafts semantically
/// (order/format-insensitive), map wire keys back to generated member names, guard
/// secret placeholders, and merge drafts over current values. Persistence always
/// flows through <see cref="EditSession{T}"/> afterwards.
/// </para>
/// <para>
/// Member access flows through generated <see cref="ConfiglueModelSchema"/>
/// metadata only; no reflection over arbitrary runtime objects.
/// </para>
/// </remarks>
internal static class ConfiglueEditorSemanticJson
{
    /// <summary>
    /// Rewrites an effective-state JSON tree so property keys use generated member
    /// names. Unknown keys are preserved verbatim so strict model binding still
    /// rejects them with a schema error instead of silently dropping them.
    /// </summary>
    public static JsonNode? NormalizeKeys(
        JsonNode? node,
        ConfiglueModelSchema schema,
        JsonNamingPolicy? namingPolicy
    )
    {
        if (node is not JsonObject obj)
        {
            // Arrays never appear at the root of an effective-state projection
            // (the root is always an object); member-level arrays are normalized
            // with their member schema by the object branch below.
            return node?.DeepClone();
        }

        var result = new JsonObject();
        foreach (var (key, child) in obj)
        {
            var member = TryFindMember(schema, key, namingPolicy);
            if (member is null)
            {
                result[key] = child?.DeepClone();
                continue;
            }

            var nested = TryGetNestedSchema(member.Value);
            if (nested is null)
            {
                result[member.Value.Name] = child?.DeepClone();
                continue;
            }

            if (child is JsonObject childObject)
            {
                result[member.Value.Name] = NormalizeKeys(childObject, nested, namingPolicy);
                continue;
            }

            if (child is JsonArray childArray)
            {
                var normalizedElements = new JsonArray();
                foreach (var element in childArray)
                {
                    normalizedElements.Add(
                        element is JsonObject elementObject
                            ? NormalizeKeys(elementObject, nested, namingPolicy)
                            : element?.DeepClone()
                    );
                }

                result[member.Value.Name] = normalizedElements;
                continue;
            }

            result[member.Value.Name] = child?.DeepClone();
        }

        return result;
    }

    /// <summary>
    /// Whether two JSON trees carry the same effective value: object key order,
    /// insignificant whitespace, and equivalent number spellings are ignored.
    /// </summary>
    public static bool SemanticEquals(JsonNode? left, JsonNode? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            if (leftObject.Count != rightObject.Count)
            {
                return false;
            }

            foreach (var (key, leftChild) in leftObject)
            {
                var match = FindKeyIgnoreCase(rightObject, key);
                if (match is null || !SemanticEquals(leftChild, rightObject[match]))
                {
                    return false;
                }
            }

            return true;
        }

        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            if (leftArray.Count != rightArray.Count)
            {
                return false;
            }

            for (var index = 0; index < leftArray.Count; index++)
            {
                if (!SemanticEquals(leftArray[index], rightArray[index]))
                {
                    return false;
                }
            }

            return true;
        }

        if (left is JsonValue leftValue && right is JsonValue rightValue)
        {
            return JsonValuesEqual(leftValue, rightValue);
        }

        return false;
    }

    /// <summary>
    /// Collects changed leaf paths (generated member names, collection elements as
    /// <c>Tags[0]</c>) between a normalized baseline and a normalized draft.
    /// </summary>
    public static void CollectChangedPaths(
        JsonNode? baseline,
        JsonNode? draft,
        ConfiglueModelSchema schema,
        string prefix,
        List<string> into
    )
    {
        ArgumentNullException.ThrowIfNull(into);
        if (SemanticEquals(baseline, draft))
        {
            return;
        }

        if (baseline is JsonObject baseObject && draft is JsonObject draftObject)
        {
            foreach (var (key, draftChild) in draftObject)
            {
                var match = FindKeyIgnoreCase(baseObject, key);
                var childPrefix = prefix.Length == 0 ? key : prefix + "." + key;
                if (match is null)
                {
                    into.Add(childPrefix);
                    continue;
                }

                var member = TryFindMember(schema, key, namingPolicy: null);
                var nested = member is null ? null : TryGetNestedSchema(member.Value);
                if (nested is null)
                {
                    if (!SemanticEquals(baseObject[match], draftChild))
                    {
                        into.Add(childPrefix);
                    }

                    continue;
                }

                if (baseObject[match] is JsonArray baseArray && draftChild is JsonArray draftArray)
                {
                    CollectArrayChanges(baseArray, draftArray, nested, childPrefix, into);
                    continue;
                }

                CollectChangedPaths(baseObject[match], draftChild, nested, childPrefix, into);
            }

            foreach (var (key, _) in baseObject)
            {
                if (FindKeyIgnoreCase(draftObject, key) is null)
                {
                    into.Add(prefix.Length == 0 ? key : prefix + "." + key);
                }
            }

            return;
        }

        if (baseline is JsonArray rootBase && draft is JsonArray rootDraft)
        {
            // Root-level arrays never occur in effective-state projections; the
            // member-level array path below carries the element schema.
            CollectArrayChanges(rootBase, rootDraft, elementSchema: null, prefix, into);
            return;
        }

        into.Add(prefix);
    }

    private static void CollectArrayChanges(
        JsonArray baseline,
        JsonArray draft,
        ConfiglueModelSchema? elementSchema,
        string prefix,
        List<string> into
    )
    {
        if (baseline.Count != draft.Count)
        {
            into.Add(prefix);
        }

        var shared = Math.Min(baseline.Count, draft.Count);
        for (var index = 0; index < shared; index++)
        {
            var elementPrefix =
                $"{prefix}[{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";
            if (elementSchema is null)
            {
                if (!SemanticEquals(baseline[index], draft[index]))
                {
                    into.Add(elementPrefix);
                }
            }
            else
            {
                CollectChangedPaths(
                    baseline[index],
                    draft[index],
                    elementSchema,
                    elementPrefix,
                    into
                );
            }
        }
    }

    /// <summary>
    /// Validates secret placeholders inside a normalized draft against a normalized
    /// baseline. Any non-placeholder value at a secret position is plaintext that
    /// must never enter the Monaco model; any deleted placeholder is a protected
    /// range removal.
    /// </summary>
    public static void ValidateDraftSecrets(
        ConfiglueModelSchema schema,
        JsonObject? draft,
        JsonObject? baseline,
        string prefix,
        bool ancestorSecret,
        List<string> plaintextViolations,
        List<string> deletedPlaceholders
    )
    {
        ArgumentNullException.ThrowIfNull(plaintextViolations);
        ArgumentNullException.ThrowIfNull(deletedPlaceholders);
        foreach (var member in schema.Members)
        {
            if (member.IsDefault)
            {
                continue;
            }

            var path = prefix.Length == 0 ? member.Name : prefix + "." + member.Name;
            var secretHere = ancestorSecret || member.IsSecret;
            var hasDraft = draft is not null && draft.ContainsKey(member.Name);
            var draftChild = hasDraft ? draft![member.Name] : null;
            var hasBaseline = baseline is not null && baseline.ContainsKey(member.Name);

            if (secretHere)
            {
                if (!hasDraft)
                {
                    if (hasBaseline)
                    {
                        deletedPlaceholders.Add(path);
                    }
                }
                else if (draftChild is null || !IsPlaceholder(draftChild))
                {
                    // Explicit JSON null or any other value at a secret position is
                    // never a legitimate draft edit; secrets change only through
                    // the explicit secret-change flow.
                    plaintextViolations.Add(path);
                }

                continue;
            }

            if (!hasDraft && !hasBaseline)
            {
                continue;
            }

            var nested = TryGetNestedSchema(member);
            if (nested is null)
            {
                continue;
            }

            // Only an object-vs-anything draft needs a per-member secret walk.
            // A missing key resets to default and an explicit null sets null;
            // both are wholesale operations handled downstream, never silent
            // placeholder deletions.
            if (draftChild is JsonObject draftObject)
            {
                ValidateDraftSecrets(
                    nested,
                    draftObject,
                    baseline?[member.Name] as JsonObject,
                    path,
                    ancestorSecret: false,
                    plaintextViolations,
                    deletedPlaceholders
                );
            }
        }
    }

    /// <summary>
    /// Merges a normalized draft over the current value. Secret placeholders are
    /// resolved to current values (or dropped when nothing is known, so a fake
    /// placeholder is never written); everything else is taken from the draft.
    /// Original draft keys are preserved so explicit wire names keep binding.
    /// </summary>
    public static JsonObject MergeDraft(
        JsonObject? current,
        JsonObject draft,
        ConfiglueModelSchema schema,
        JsonNamingPolicy? namingPolicy
    )
    {
        ArgumentNullException.ThrowIfNull(draft);
        var result = new JsonObject();
        foreach (var (key, draftChild) in draft)
        {
            var member = TryFindMember(schema, key, namingPolicy);
            if (member is null)
            {
                // Unknown or explicitly-named members pass through; strict model
                // binding rejects the truly unknown ones afterwards.
                result[key] = draftChild?.DeepClone();
                continue;
            }

            if (member.Value.IsSecret && IsPlaceholder(draftChild))
            {
                var preserved = FindChild(current, member.Value, namingPolicy)?.DeepClone();
                if (preserved is not null)
                {
                    result[key] = preserved;
                }
                // Without a known current secret the placeholder is dropped so a
                // fake "********" value is never written through normal routing.

                continue;
            }

            var nested = TryGetNestedSchema(member.Value);
            if (nested is not null && draftChild is JsonObject draftObject)
            {
                result[key] = MergeDraft(
                    FindChild(current, member.Value, namingPolicy) as JsonObject,
                    draftObject,
                    nested,
                    namingPolicy
                );
                continue;
            }

            result[key] = draftChild?.DeepClone();
        }

        return result;
    }

    /// <summary>
    /// Whether a node is the redacted secret placeholder (and nothing else).
    /// </summary>
    public static bool IsPlaceholder(JsonNode? node) =>
        node is JsonValue value
        && value.TryGetValue<string>(out var text)
        && string.Equals(text, ConfiglueSecrets.RedactedText, StringComparison.Ordinal);

    /// <summary>
    /// Whether any secret-flagged subtree differs between two boxed model values,
    /// using generated getters and member-wise serialization. Key-name independent,
    /// so explicitly-named wire members are covered without reflection.
    /// </summary>
    public static bool SecretSubtreeDiffers(
        ConfiglueModelSchema schema,
        object? current,
        object? desired,
        string prefix,
        bool ancestorSecret,
        out string diffPath
    )
    {
        diffPath = string.Empty;
        if (ReferenceEquals(current, desired))
        {
            return false;
        }

        if (current is null || desired is null)
        {
            // A null transition at a non-secret position is an explicit
            // set-null/reset handled by normal model semantics, never secret
            // smuggling: null drafts at secret positions are rejected earlier,
            // and placeholders that would persist are caught by value comparison.
            return false;
        }

        if (ancestorSecret)
        {
            if (!SerializedEquals(current, desired))
            {
                diffPath = prefix;
                return true;
            }

            return false;
        }

        return SecretChildDiffers(schema, current, desired, prefix, out diffPath);
    }

    private static bool SecretChildDiffers(
        ConfiglueModelSchema schema,
        object? current,
        object? desired,
        string prefix,
        out string diffPath
    )
    {
        diffPath = string.Empty;
        foreach (var member in schema.Members)
        {
            if (member.IsDefault || member.GetValue is null)
            {
                continue;
            }

            var path = prefix.Length == 0 ? member.Name : prefix + "." + member.Name;
            object? currentChild;
            object? desiredChild;
            try
            {
                currentChild = current is null ? null : member.GetValue(current);
                desiredChild = desired is null ? null : member.GetValue(desired);
            }
            catch (Exception)
            {
                // A failing generated getter is a read problem, not a proven secret
                // change; server validation reports the underlying issue instead.
                continue;
            }

            var secretHere = member.IsSecret;
            if (secretHere)
            {
                if (!SerializedEquals(currentChild, desiredChild))
                {
                    diffPath = path;
                    return true;
                }

                continue;
            }

            var nested = TryGetNestedSchema(member);
            if (nested is null || (currentChild is null && desiredChild is null))
            {
                continue;
            }

            if (
                currentChild is System.Collections.IEnumerable currentItems
                && desiredChild is System.Collections.IEnumerable desiredItems
                && currentChild is not string
                && desiredChild is not string
            )
            {
                var left = currentItems.Cast<object?>().ToArray();
                var right = desiredItems.Cast<object?>().ToArray();
                if (left.Length != right.Length)
                {
                    // Length changes flow through normal collection merge semantics;
                    // only element-level secret differences are guarded here.
                    for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
                    {
                        if (
                            SecretSubtreeDiffers(
                                nested,
                                left[index],
                                right[index],
                                $"{path}[{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}]",
                                ancestorSecret: false,
                                out diffPath
                            )
                        )
                        {
                            return true;
                        }
                    }

                    continue;
                }

                for (var index = 0; index < left.Length; index++)
                {
                    if (
                        SecretSubtreeDiffers(
                            nested,
                            left[index],
                            right[index],
                            $"{path}[{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}]",
                            ancestorSecret: false,
                            out diffPath
                        )
                    )
                    {
                        return true;
                    }
                }

                continue;
            }

            if (
                SecretSubtreeDiffers(
                    nested,
                    currentChild,
                    desiredChild,
                    path,
                    ancestorSecret: false,
                    out diffPath
                )
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool SerializedEquals(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        try
        {
            return SemanticEquals(
                JsonSerializer.SerializeToNode(left),
                JsonSerializer.SerializeToNode(right)
            );
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool JsonValuesEqual(JsonValue left, JsonValue right)
    {
        var leftKind = left.GetValueKind();
        var rightKind = right.GetValueKind();
        if (leftKind != rightKind)
        {
            return false;
        }

        return leftKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.True => true,
            JsonValueKind.False => true,
            JsonValueKind.String => string.Equals(
                left.GetValue<string>(),
                right.GetValue<string>(),
                StringComparison.Ordinal
            ),
            JsonValueKind.Number => left.ToJsonString()
                .Equals(right.ToJsonString(), StringComparison.Ordinal)
                || (
                    left.TryGetValue<decimal>(out var leftNumber)
                    && right.TryGetValue<decimal>(out var rightNumber)
                    && leftNumber == rightNumber
                ),
            _ => left.ToJsonString().Equals(right.ToJsonString(), StringComparison.Ordinal),
        };
    }

    private static string? FindKeyIgnoreCase(JsonObject obj, string key)
    {
        if (obj.ContainsKey(key))
        {
            return key;
        }

        foreach (var (candidate, _) in obj)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static ConfiglueMemberSchema? TryFindMember(
        ConfiglueModelSchema schema,
        string key,
        JsonNamingPolicy? namingPolicy
    )
    {
        foreach (var member in schema.Members)
        {
            if (member.IsDefault)
            {
                continue;
            }

            if (string.Equals(member.Name, key, StringComparison.Ordinal))
            {
                return member;
            }
        }

        if (namingPolicy is not null)
        {
            foreach (var member in schema.Members)
            {
                if (member.IsDefault)
                {
                    continue;
                }

                string? converted = null;
                try
                {
                    converted = namingPolicy.ConvertName(member.Name);
                }
                catch (Exception)
                {
                    converted = null;
                }

                if (
                    converted is not null
                    && string.Equals(converted, key, StringComparison.Ordinal)
                )
                {
                    return member;
                }
            }
        }

        foreach (var member in schema.Members)
        {
            if (member.IsDefault)
            {
                continue;
            }

            if (string.Equals(member.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return member;
            }

            if (namingPolicy is not null)
            {
                string? converted = null;
                try
                {
                    converted = namingPolicy.ConvertName(member.Name);
                }
                catch (Exception)
                {
                    converted = null;
                }

                if (
                    converted is not null
                    && string.Equals(converted, key, StringComparison.OrdinalIgnoreCase)
                )
                {
                    return member;
                }
            }
        }

        return null;
    }

    private static JsonNode? FindChild(
        JsonObject? obj,
        ConfiglueMemberSchema member,
        JsonNamingPolicy? namingPolicy
    )
    {
        if (obj is null)
        {
            return null;
        }

        if (obj.TryGetPropertyValue(member.Name, out var child))
        {
            return child;
        }

        if (namingPolicy?.ConvertName(member.Name) is string converted)
        {
            try
            {
                if (obj.TryGetPropertyValue(converted, out child))
                {
                    return child;
                }
            }
            catch (Exception)
            {
                // Naming-policy failures fall through to the scan below.
            }
        }

        foreach (var (candidate, value) in obj)
        {
            if (string.Equals(candidate, member.Name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static ConfiglueModelSchema? TryGetNestedSchema(ConfiglueMemberSchema member)
    {
        if (member.NestedSchemaFactory is null)
        {
            return null;
        }

        try
        {
            return member.NestedSchemaFactory();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
