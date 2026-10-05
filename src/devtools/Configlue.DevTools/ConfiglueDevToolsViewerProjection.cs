using System.Text.Json;
using System.Text.Json.Nodes;
using Configlue.CompilerServices;

namespace Configlue.DevTools;

/// <summary>
/// Builds the canonical effective-state viewer document for DevTools with
/// <c>#244</c> secret redaction and generated-details provenance.
/// </summary>
/// <remarks>
/// <para>
/// Public facade only. Document emission is owned by
/// <see cref="ConfiglueViewerDocumentEmitter"/> over a per-operation
/// <see cref="ConfiglueViewerEmissionContext"/>; provenance and hover
/// annotation by <see cref="ConfiglueViewerProvenanceAnnotator"/>; range
/// bookkeeping by <see cref="ConfiglueViewerPositionWriter"/>; JSON Schema
/// generation by <see cref="ConfiglueViewerSchemaEmitter"/>.
/// </para>
/// <para>
/// Internal to the DevTools package. All member access flows through generated
/// <see cref="ConfiglueModelSchema"/> metadata and the details-snapshot
/// transport behind generated <c>GetDetailsAsync()</c>; no reflection over
/// arbitrary runtime objects and no new inspection API. Ranges are emitted
/// alongside the projection so decorations never rescan the full document.
/// </para>
/// </remarks>
internal static class ConfiglueDevToolsViewerProjection
{
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
        var context = new ConfiglueViewerEmissionContext(
            snapshot,
            options.NamingPolicy,
            options.Indent
        );
        var rootPath = ConfiglueMemberPath.Root(schema);

        context.Writer.Append("{");
        var rootObject = rootNode as JsonObject;
        ConfiglueViewerDocumentEmitter.EmitObject(
            schema,
            rootObject,
            value,
            rootPath,
            string.Empty,
            isSecretAncestor: false,
            depth: 1,
            context
        );
        context.Writer.Append("}");

        return context.ToDocument(
            schema.Id,
            schema.Version,
            documentVersion,
            BuildSchemaUri(schema)
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
            ConfiglueViewerSchemaEmitter.BuildSchemaJson(schema, options)
        );
    }

    public static string BuildSchemaUri(ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return $"configlue://schemas/{schema.Id}/{schema.Version}";
    }

    /// <summary>
    /// Builds the explicit Monaco document URI for one bound state instance.
    /// </summary>
    /// <remarks>
    /// Development-only viewer metadata. The document URI identifies the state
    /// instance (<c>modelId</c> plus <c>stateName</c>) and is distinct from the
    /// schema URI: the JSON language-service <c>fileMatch</c> targets this
    /// document URI, and runtime markers target the editor model carrying it.
    /// An empty state name maps to <c>-</c> so the URI stays well-formed.
    /// </remarks>
    public static string BuildDocumentUri(string modelId, string stateName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var name = string.IsNullOrEmpty(stateName) ? "-" : stateName;
        return $"configlue://states/{Uri.EscapeDataString(modelId)}/{Uri.EscapeDataString(name)}";
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
        return ConfiglueViewerProvenanceAnnotator.BuildContributionJson(
            snapshot,
            schema,
            memberPath
        );
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
}
