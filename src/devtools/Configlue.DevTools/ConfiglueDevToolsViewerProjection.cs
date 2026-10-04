using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configlue.CompilerServices;

namespace Configlue.DevTools;

/// <summary>
/// Builds the canonical effective-state viewer document for DevTools with
/// <c>#244</c> secret redaction and generated-details provenance.
/// </summary>
/// <remarks>
/// Internal to the DevTools package. All member access flows through generated
/// <see cref="ConfiglueModelSchema"/> metadata and the details-snapshot
/// transport behind generated <c>GetDetailsAsync()</c>; no reflection over
/// arbitrary runtime objects and no new inspection API. Ranges are emitted
/// alongside the projection so decorations never rescan the full document.
/// </remarks>
internal static class ConfiglueDevToolsViewerProjection
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    public static ConfiglueViewerDocument BuildDocument<TModel>(
        TModel? value,
        ConfiglueDetailsSnapshot? snapshot,
        ConfiglueModelSchema schema,
        ConfiglueDevToolsViewerOptions? options,
        long documentVersion
    )
    {
        options ??= ConfiglueDevToolsViewerOptions.Default;
        var serialize = new JsonSerializerOptions { PropertyNamingPolicy = options.NamingPolicy };
        var rootNode = JsonSerializer.SerializeToNode(value, serialize);
        var writer = new PositionWriter(options.Indent);
        var memberRanges = new List<ConfiglueViewerMemberRange>();
        var decorations = new List<ConfiglueViewerDecoration>();
        var hovers = new List<ConfiglueViewerHover>();
        var markers = new List<ConfiglueViewerMarker>();
        var rootPath = ConfiglueMemberPath.Root(schema);

        writer.Append("{");
        var rootObject = rootNode as JsonObject;
        EmitObject(
            schema,
            rootObject,
            value,
            snapshot,
            rootPath,
            string.Empty,
            writer,
            memberRanges,
            decorations,
            hovers,
            markers,
            options,
            serialize,
            isSecretAncestor: false,
            depth: 1
        );
        writer.Append("}");

        return new ConfiglueViewerDocument(
            schema.Id,
            schema.Version,
            documentVersion,
            writer.ToString(),
            BuildSchemaUri(schema),
            memberRanges.AsReadOnly(),
            decorations.AsReadOnly(),
            hovers.AsReadOnly(),
            markers.AsReadOnly()
        );
    }

    public static ConfiglueViewerSchemaSetup BuildSchemaSetup(
        ConfiglueModelSchema schema,
        ConfiglueDevToolsViewerOptions? options
    )
    {
        options ??= ConfiglueDevToolsViewerOptions.Default;
        return new ConfiglueViewerSchemaSetup(
            schema.Id,
            schema.Version,
            BuildSchemaUri(schema),
            BuildSchemaJson(schema, options)
        );
    }

    public static string BuildSchemaUri(ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return $"configlue://schemas/{schema.Id}/{schema.Version}";
    }

    /// <summary>
    /// Whether two viewer documents carry identical overlay content.
    /// Hover contribution lists are compared element-wise because record
    /// equality does not descend into collection properties.
    /// </summary>
    public static bool AreOverlaysEqual(
        ConfiglueViewerDocument first,
        ConfiglueViewerDocument second
    )
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.Decorations.SequenceEqual(second.Decorations)
            && first.Markers.SequenceEqual(second.Markers)
            && first.Hovers.Count == second.Hovers.Count
            && first
                .Hovers.Zip(
                    second.Hovers,
                    static (left, right) =>
                        string.Equals(left.MemberPath, right.MemberPath, StringComparison.Ordinal)
                        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
                        && string.Equals(left.Markdown, right.Markdown, StringComparison.Ordinal)
                        && string.Equals(
                            left.EffectiveSource,
                            right.EffectiveSource,
                            StringComparison.Ordinal
                        )
                        && string.Equals(
                            left.Editability,
                            right.Editability,
                            StringComparison.Ordinal
                        )
                        && left.IsSecret == right.IsSecret
                        && string.Equals(left.Locator, right.Locator, StringComparison.Ordinal)
                        && left.Contributions.SequenceEqual(right.Contributions)
                )
                .All(static equal => equal);
    }

    /// <summary>
    /// Computes minimal line-oriented edits from old JSON to new JSON.
    /// Identical text yields no edits so decoration-only refreshes carry no
    /// document payload. Edits (not full <c>SetValue</c>) preserve
    /// scroll/selection in Monaco.
    /// </summary>
    public static IReadOnlyList<ConfiglueViewerTextEdit> ComputeTextEdits(
        string oldJson,
        string newJson
    )
    {
        if (string.Equals(oldJson, newJson, StringComparison.Ordinal))
        {
            return [];
        }

        var oldLines = SplitLines(oldJson);
        var newLines = SplitLines(newJson);
        var prefix = 0;
        while (
            prefix < oldLines.Count
            && prefix < newLines.Count
            && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal)
        )
        {
            prefix++;
        }

        var suffix = 0;
        while (
            suffix < oldLines.Count - prefix
            && suffix < newLines.Count - prefix
            && string.Equals(
                oldLines[oldLines.Count - 1 - suffix],
                newLines[newLines.Count - 1 - suffix],
                StringComparison.Ordinal
            )
        )
        {
            suffix++;
        }

        var startLine = prefix + 1;
        var endLine = oldLines.Count - suffix;
        var endColumn = oldLines.Count == 0 ? 1 : oldLines[endLine - 1].Length + 1;
        var replacement = string.Join(
            "\n",
            newLines.Skip(prefix).Take(newLines.Count - prefix - suffix)
        );
        if (suffix > 0 || replacement.Length > 0)
        {
            // Keep the edit anchored on line starts so trailing newlines survive.
        }

        return
        [
            new ConfiglueViewerTextEdit(
                new ConfiglueViewerRange(startLine, 1, endLine, endColumn),
                replacement
            ),
        ];
    }

    /// <summary>
    /// Normalized per-member contribution projection (compact metadata only).
    /// Never a raw source document; secret values stay redacted.
    /// </summary>
    public static string BuildContributionJson(
        ConfiglueDetailsSnapshot snapshot,
        ConfiglueModelSchema schema,
        string memberPath
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberPath);
        var path = ConfiglueMemberPath.FromNames(schema, memberPath);
        var provenance = ComputeProvenance(snapshot, path);
        var isSecret = path.IsSecret();
        var contributions = snapshot.Sources.Select(
            (source, index) =>
            {
                object? raw = null;
                var present =
                    snapshot.SourceFragments[index] is not null
                    && path.TryGetFragmentValue(snapshot.SourceFragments[index], out raw);
                JsonNode? valueNode = null;
                if (present)
                {
                    valueNode = isSecret
                        ? JsonValue.Create(ConfiglueSecrets.RedactedText)
                        : SerializeValueSafe(raw);
                }

                return new
                {
                    sourceKey = source.Key,
                    displayName = source.DisplayName,
                    kind = source.Kind,
                    state = provenance.States[index].ToString(),
                    present,
                    effective = provenance.EffectiveIndex == index,
                    shadowed = provenance.Shadowed[index],
                    locator = source.Locator,
                    value = valueNode,
                };
            }
        );

        return JsonSerializer.Serialize(
            new
            {
                memberPath,
                wireName = memberPath.Split('.')[^1],
                isSecret,
                effectiveSource = provenance.Effective?.DisplayName,
                contributions,
            },
            new JsonSerializerOptions { WriteIndented = true }
        );
    }

    private static void EmitObject(
        ConfiglueModelSchema schema,
        JsonObject? node,
        object? modelValue,
        ConfiglueDetailsSnapshot? snapshot,
        ConfiglueMemberPath pathPrefix,
        string pathNamePrefix,
        PositionWriter writer,
        List<ConfiglueViewerMemberRange> memberRanges,
        List<ConfiglueViewerDecoration> decorations,
        List<ConfiglueViewerHover> hovers,
        List<ConfiglueViewerMarker> markers,
        ConfiglueDevToolsViewerOptions options,
        JsonSerializerOptions serialize,
        bool isSecretAncestor,
        int depth
    )
    {
        var emitted = 0;
        foreach (var member in schema.Members)
        {
            if (member.IsDefault)
            {
                continue;
            }

            var wireName = ResolveWireName(member, node, options.NamingPolicy, out var childNode);
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
            object? memberValue = null;
            if (modelValue is not null && member.GetValue is not null)
            {
                try
                {
                    memberValue = member.GetValue(modelValue);
                }
                catch (Exception)
                {
                    memberValue = null;
                }
            }

            if (emitted > 0)
            {
                writer.Append(",");
            }

            writer.AppendLine();
            writer.AppendIndent(depth);
            var nameStart = writer.Position;
            writer.AppendQuoted(wireName);
            var nameEnd = writer.Position;
            writer.Append(": ");

            var valueStart = writer.Position;
            EmitValue(
                member,
                childNode,
                memberValue,
                snapshot,
                path,
                memberPath,
                writer,
                memberRanges,
                decorations,
                hovers,
                markers,
                options,
                serialize,
                secret,
                depth
            );
            var valueEnd = writer.Position;

            memberRanges.Add(
                new ConfiglueViewerMemberRange(
                    memberPath,
                    wireName,
                    new ConfiglueViewerRange(
                        nameStart.Line,
                        nameStart.Column,
                        nameEnd.Line,
                        nameEnd.Column
                    ),
                    new ConfiglueViewerRange(
                        valueStart.Line,
                        valueStart.Column,
                        valueEnd.Line,
                        valueEnd.Column
                    ),
                    secret
                )
            );

            if (snapshot is not null)
            {
                AppendProvenance(
                    snapshot,
                    member,
                    path,
                    memberPath,
                    new ConfiglueViewerRange(
                        valueStart.Line,
                        valueStart.Column,
                        valueEnd.Line,
                        valueEnd.Column
                    ),
                    decorations,
                    hovers,
                    markers
                );
            }

            emitted++;
        }

        if (emitted > 0)
        {
            writer.AppendLine();
            writer.AppendIndent(depth - 1);
        }
    }

    private static void EmitValue(
        ConfiglueMemberSchema member,
        JsonNode? childNode,
        object? memberValue,
        ConfiglueDetailsSnapshot? snapshot,
        ConfiglueMemberPath path,
        string memberPath,
        PositionWriter writer,
        List<ConfiglueViewerMemberRange> memberRanges,
        List<ConfiglueViewerDecoration> decorations,
        List<ConfiglueViewerHover> hovers,
        List<ConfiglueViewerMarker> markers,
        ConfiglueDevToolsViewerOptions options,
        JsonSerializerOptions serialize,
        bool secret,
        int depth
    )
    {
        if (secret)
        {
            writer.AppendQuoted(ConfiglueSecrets.RedactedText);
            return;
        }

        if (
            childNode is null
            || (childNode is JsonValue nullValue && nullValue.GetValueKind() == JsonValueKind.Null)
        )
        {
            writer.Append("null");
            return;
        }

        if (member.NestedSchemaFactory is not null && childNode is JsonObject childObject)
        {
            var nestedSchema = member.NestedSchemaFactory();
            writer.Append("{");
            EmitObject(
                nestedSchema,
                childObject,
                memberValue,
                snapshot,
                path,
                memberPath,
                writer,
                memberRanges,
                decorations,
                hovers,
                markers,
                options,
                serialize,
                isSecretAncestor: false,
                depth: depth + 1
            );
            writer.Append("}");
            return;
        }

        if (childNode is JsonArray array)
        {
            EmitArray(
                member,
                array,
                memberValue,
                snapshot,
                path,
                memberPath,
                writer,
                memberRanges,
                decorations,
                hovers,
                markers,
                options,
                serialize,
                depth
            );
            return;
        }

        writer.Append(childNode.ToJsonString());
    }

    private static void EmitArray(
        ConfiglueMemberSchema member,
        JsonArray array,
        object? memberValue,
        ConfiglueDetailsSnapshot? snapshot,
        ConfiglueMemberPath path,
        string memberPath,
        PositionWriter writer,
        List<ConfiglueViewerMemberRange> memberRanges,
        List<ConfiglueViewerDecoration> decorations,
        List<ConfiglueViewerHover> hovers,
        List<ConfiglueViewerMarker> markers,
        ConfiglueDevToolsViewerOptions options,
        JsonSerializerOptions serialize,
        int depth
    )
    {
        if (array.Count == 0)
        {
            writer.Append("[]");
            return;
        }

        var nestedSchema =
            member.NestedSchemaFactory is not null
            && array.Any(static element => element is JsonObject)
                ? member.NestedSchemaFactory()
                : null;
        IReadOnlyList<ConfigCollectionElementData>? elementData = snapshot?.CollectionElements(
            path
        );
        writer.Append("[");
        for (var index = 0; index < array.Count; index++)
        {
            if (index > 0)
            {
                writer.Append(",");
            }

            writer.AppendLine();
            writer.AppendIndent(depth + 1);
            var element = array[index];
            var elementPath = $"{memberPath}[{index}]";
            var elementStart = writer.Position;
            if (
                nestedSchema is not null
                && element is JsonObject elementObject
                && memberValue is System.Collections.IEnumerable values
            )
            {
                var elementValue = ElementAt(values, index);
                writer.Append("{");
                EmitObject(
                    nestedSchema,
                    elementObject,
                    elementValue,
                    snapshot,
                    path,
                    elementPath,
                    writer,
                    memberRanges,
                    decorations,
                    hovers,
                    markers,
                    options,
                    serialize,
                    isSecretAncestor: false,
                    depth: depth + 2
                );
                writer.Append("}");
            }
            else
            {
                writer.Append(element?.ToJsonString() ?? "null");
            }

            var elementEnd = writer.Position;
            // Element name ranges are intentionally empty (arrays have no property keys).
            var emptyName = new ConfiglueViewerRange(
                elementStart.Line,
                elementStart.Column,
                elementStart.Line,
                elementStart.Column
            );
            memberRanges.Add(
                new ConfiglueViewerMemberRange(
                    elementPath,
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    emptyName,
                    new ConfiglueViewerRange(
                        elementStart.Line,
                        elementStart.Column,
                        elementEnd.Line,
                        elementEnd.Column
                    ),
                    member.IsSecret
                )
            );

            if (snapshot is not null)
            {
                AppendElementProvenance(
                    snapshot,
                    path,
                    elementPath,
                    index,
                    elementData,
                    new ConfiglueViewerRange(
                        elementStart.Line,
                        elementStart.Column,
                        elementEnd.Line,
                        elementEnd.Column
                    ),
                    member.IsSecret,
                    decorations,
                    hovers
                );
            }
        }

        writer.AppendLine();
        writer.AppendIndent(depth);
        writer.Append("]");
    }

    private static void AppendProvenance(
        ConfiglueDetailsSnapshot snapshot,
        ConfiglueMemberSchema member,
        ConfiglueMemberPath path,
        string memberPath,
        ConfiglueViewerRange valueRange,
        List<ConfiglueViewerDecoration> decorations,
        List<ConfiglueViewerHover> hovers,
        List<ConfiglueViewerMarker> markers
    )
    {
        var provenance = ComputeProvenance(snapshot, path);
        var isSecret = path.IsSecret();
        var editability = snapshot.Editability(path);
        var isEditable = editability == ConfiglueEditability.Editable;

        if (provenance.Effective is not null)
        {
            decorations.Add(
                new ConfiglueViewerDecoration(
                    memberPath,
                    valueRange,
                    ConfiglueViewerDecorationKind.Effective,
                    provenance.Effective.DisplayName,
                    "configlue-effective"
                )
            );
            decorations.Add(
                new ConfiglueViewerDecoration(
                    memberPath,
                    valueRange,
                    ConfiglueViewerDecorationKind.SourceLabel,
                    CompactSourceLabel(provenance.Effective),
                    "configlue-source-label"
                )
            );
        }

        if (!isEditable)
        {
            decorations.Add(
                new ConfiglueViewerDecoration(
                    memberPath,
                    valueRange,
                    ConfiglueViewerDecorationKind.ReadOnly,
                    $"read-only · {editability}",
                    "configlue-readonly"
                )
            );
        }

        if (isSecret)
        {
            decorations.Add(
                new ConfiglueViewerDecoration(
                    memberPath,
                    valueRange,
                    ConfiglueViewerDecorationKind.Secret,
                    "secret",
                    "configlue-secret"
                )
            );
        }

        var invalidSources = provenance
            .States.Select((state, index) => (state, index))
            .Where(static entry => entry.state == ConfigSourceValueState.Invalid)
            .Select(entry => snapshot.Sources[entry.index].DisplayName)
            .ToArray();
        if (invalidSources.Length > 0)
        {
            decorations.Add(
                new ConfiglueViewerDecoration(
                    memberPath,
                    valueRange,
                    ConfiglueViewerDecorationKind.Invalid,
                    "invalid",
                    "configlue-invalid"
                )
            );
            markers.Add(
                new ConfiglueViewerMarker(
                    memberPath,
                    valueRange,
                    ConfiglueViewerMarkerSeverity.Error,
                    $"Source '{invalidSources[0]}' reported an invalid payload for '{memberPath}'.",
                    invalidSources[0]
                )
            );
        }

        hovers.Add(BuildHover(snapshot, member, path, memberPath, provenance, editability));
    }

    private static void AppendElementProvenance(
        ConfiglueDetailsSnapshot snapshot,
        ConfiglueMemberPath path,
        string elementPath,
        int elementIndex,
        IReadOnlyList<ConfigCollectionElementData>? elementData,
        ConfiglueViewerRange valueRange,
        bool isSecret,
        List<ConfiglueViewerDecoration> decorations,
        List<ConfiglueViewerHover> hovers
    )
    {
        var contributors =
            elementData?.FirstOrDefault(data => data.Index == elementIndex).SourceIndices ?? [];
        ConfigSourceDetails? effective = null;
        if (contributors.Count == 1)
        {
            effective = snapshot.Sources[contributors[0]];
            decorations.Add(
                new ConfiglueViewerDecoration(
                    elementPath,
                    valueRange,
                    ConfiglueViewerDecorationKind.Effective,
                    effective.DisplayName,
                    "configlue-effective"
                )
            );
        }

        if (isSecret)
        {
            decorations.Add(
                new ConfiglueViewerDecoration(
                    elementPath,
                    valueRange,
                    ConfiglueViewerDecorationKind.Secret,
                    "secret",
                    "configlue-secret"
                )
            );
        }

        var contributions = snapshot
            .Sources.Select(
                (source, index) =>
                {
                    var contributes = contributors.Contains(index);
                    return new ConfiglueViewerContribution(
                        source.Key,
                        source.DisplayName,
                        source.Kind,
                        contributes
                            ? ConfigSourceValueState.Present.ToString()
                            : ConfigSourceValueState.Missing.ToString(),
                        contributes && contributors.Count == 1,
                        false,
                        source.Locator
                    );
                }
            )
            .ToArray();

        string markdown;
        if (isSecret)
        {
            var presence = contributors.Count > 0 ? "Yes" : "No";
            markdown =
                $"### {elementPath}\n\nSecret: Yes\n\nPresent: {presence}\n\nEffective source: {effective?.DisplayName ?? "—"}";
        }
        else
        {
            var rows = string.Join(
                "\n",
                contributions.Select(static c => $"- {c.DisplayName}: {c.State}")
            );
            markdown =
                $"### {elementPath}\n\nEffective source: {effective?.DisplayName ?? "—"}\n\nContributions\n\n{rows}";
        }
        hovers.Add(
            new ConfiglueViewerHover(
                elementPath,
                elementPath,
                markdown,
                effective?.DisplayName,
                snapshot.Editability(path).ToString(),
                isSecret,
                contributions,
                snapshot.Sources.FirstOrDefault()?.Locator
            )
        );
    }

    private static ConfiglueViewerHover BuildHover(
        ConfiglueDetailsSnapshot snapshot,
        ConfiglueMemberSchema member,
        ConfiglueMemberPath path,
        string memberPath,
        MemberProvenance provenance,
        ConfiglueEditability editability
    )
    {
        var isSecret = path.IsSecret();
        var contributions = snapshot
            .Sources.Select(
                (source, index) =>
                    new ConfiglueViewerContribution(
                        source.Key,
                        source.DisplayName,
                        source.Kind,
                        provenance.States[index].ToString(),
                        provenance.EffectiveIndex == index,
                        provenance.Shadowed[index],
                        source.Locator
                    )
            )
            .ToArray();
        var locator =
            member.EnvironmentVariableName
            ?? contributions.FirstOrDefault(static c => c.IsEffective)?.Locator
            ?? snapshot.Sources.FirstOrDefault()?.Locator;

        string markdown;
        if (isSecret)
        {
            var present = contributions.Any(static c =>
                string.Equals(
                    c.State,
                    ConfigSourceValueState.Present.ToString(),
                    StringComparison.Ordinal
                )
            );
            markdown =
                $"### {member.Name}\n\nSecret: Yes\n\nPresent: {(present ? "Yes" : "No")}\n\nEffective source: {provenance.Effective?.DisplayName ?? "—"}";
        }
        else
        {
            var lines = contributions.Select(static c =>
                $"- {c.DisplayName}: {c.State}{(c.IsEffective ? ", Effective" : string.Empty)}{(c.IsShadowed ? ", Shadowed" : string.Empty)}"
            );
            markdown =
                $"### {member.Name}\n\nEffective source: {provenance.Effective?.DisplayName ?? "—"}\n\nEditable: {(editability == ConfiglueEditability.Editable ? "Yes" : $"No ({editability})")}\n\nContributions\n\n{string.Join("\n", lines)}\n\nLocator\n\n{locator ?? "—"}";
        }

        return new ConfiglueViewerHover(
            memberPath,
            member.Name,
            markdown,
            provenance.Effective?.DisplayName,
            editability.ToString(),
            isSecret,
            contributions,
            locator
        );
    }

    private sealed record MemberProvenance(
        ConfigSourceDetails? Effective,
        int EffectiveIndex,
        ConfigSourceValueState[] States,
        bool[] Shadowed,
        HashSet<int> PresentIndices
    );

    private static MemberProvenance ComputeProvenance(
        ConfiglueDetailsSnapshot snapshot,
        ConfiglueMemberPath path
    )
    {
        var count = snapshot.Sources.Count;
        var states = new ConfigSourceValueState[count];
        var shadowed = new bool[count];
        var presentIndices = new HashSet<int>();
        ConfigSourceDetails? effective = null;
        var effectiveIndex = -1;
        for (var index = 0; index < count; index++)
        {
            var present =
                snapshot.SourceFragments[index] is not null
                && path.TryGetFragmentValue(snapshot.SourceFragments[index], out _);
            if (present)
            {
                presentIndices.Add(index);
                states[index] = ConfigSourceValueState.Present;
                shadowed[index] = effective is not null;
                if (effective is null)
                {
                    effective = snapshot.Sources[index];
                    effectiveIndex = index;
                }
            }
            else
            {
                states[index] = MapStatus(snapshot.SourceStatuses[index]);
            }
        }

        return new MemberProvenance(effective, effectiveIndex, states, shadowed, presentIndices);
    }

    private static ConfigSourceValueState MapStatus(StateReadStatus status) =>
        status switch
        {
            StateReadStatus.Unavailable => ConfigSourceValueState.Unavailable,
            StateReadStatus.InvalidPayload => ConfigSourceValueState.Invalid,
            _ => ConfigSourceValueState.Missing,
        };

    private static string CompactSourceLabel(ConfigSourceDetails source) =>
        string.IsNullOrEmpty(source.Locator)
            ? source.DisplayName
            : $"{source.DisplayName} · {source.Locator}";

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

    private static JsonNode? SerializeValueSafe(object? value)
    {
        try
        {
            return JsonSerializer.SerializeToNode(value, SerializeOptions);
        }
        catch (Exception)
        {
            return null;
        }
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

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                lines.Add(
                    text.Substring(
                        start,
                        index - start + (index > start && text[index - 1] == '\r' ? -1 : 0)
                    )
                );
                start = index + 1;
            }
        }

        lines.Add(text.Substring(start).TrimEnd('\r'));
        return lines;
    }

    private static string BuildSchemaJson(
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

    private sealed class PositionWriter
    {
        private readonly StringBuilder _builder = new();
        private readonly string _indent;

        public PositionWriter(string indent)
        {
            _indent = string.IsNullOrEmpty(indent) ? "  " : indent;
        }

        public int Line { get; private set; } = 1;

        public int Column { get; private set; } = 1;

        public (int Line, int Column) Position => (Line, Column);

        public void Append(string text)
        {
            foreach (var ch in text)
            {
                _builder.Append(ch);
                if (ch == '\n')
                {
                    Line++;
                    Column = 1;
                }
                else if (ch != '\r')
                {
                    Column++;
                }
            }
        }

        public void AppendLine()
        {
            _builder.Append('\n');
            Line++;
            Column = 1;
        }

        public void AppendIndent(int depth)
        {
            for (var index = 0; index < depth; index++)
            {
                Append(_indent);
            }
        }

        public void AppendQuoted(string value)
        {
            Append(JsonSerializer.Serialize(value, SerializeOptions));
        }

        public override string ToString() => _builder.ToString();
    }
}
