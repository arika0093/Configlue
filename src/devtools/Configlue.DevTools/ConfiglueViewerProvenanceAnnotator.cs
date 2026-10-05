using System.Text.Json;
using System.Text.Json.Nodes;
using Configlue.CompilerServices;

namespace Configlue.DevTools;

/// <summary>
/// Provenance and editability annotation for viewer projections.
/// </summary>
/// <remarks>
/// <para>
/// Owns effective-source computation, decoration/hover/marker annotation,
/// and the normalized per-member contribution projection. Writes only to
/// the overlay accumulators on <see cref="ConfiglueViewerEmissionContext"/>;
/// never to the JSON document text itself.
/// </para>
/// <para>
/// Secret safety: hover markdown and decoration labels carry presence and
/// source identity only; secret plaintext is never copied into overlays.
/// </para>
/// </remarks>
internal static class ConfiglueViewerProvenanceAnnotator
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    public static void AnnotateMember(
        ConfiglueViewerEmissionContext context,
        ConfiglueMemberSchema member,
        ConfiglueMemberPath path,
        string memberPath,
        ConfiglueViewerRange valueRange
    )
    {
        var snapshot = context.Snapshot;
        if (snapshot is null)
        {
            return;
        }

        var provenance = ComputeProvenance(snapshot, path);
        var isSecret = path.IsSecret();
        var editability = snapshot.Editability(path);
        var isEditable = editability == ConfiglueEditability.Editable;

        if (provenance.Effective is not null)
        {
            context.Decorations.Add(
                new ConfiglueViewerDecoration(
                    memberPath,
                    valueRange,
                    ConfiglueViewerDecorationKind.Effective,
                    provenance.Effective.DisplayName,
                    "configlue-effective"
                )
            );
            context.Decorations.Add(
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
            context.Decorations.Add(
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
            context.Decorations.Add(
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
            context.Decorations.Add(
                new ConfiglueViewerDecoration(
                    memberPath,
                    valueRange,
                    ConfiglueViewerDecorationKind.Invalid,
                    "invalid",
                    "configlue-invalid"
                )
            );
            context.Markers.Add(
                new ConfiglueViewerMarker(
                    memberPath,
                    valueRange,
                    ConfiglueViewerMarkerSeverity.Error,
                    $"Source '{invalidSources[0]}' reported an invalid payload for '{memberPath}'.",
                    invalidSources[0]
                )
            );
        }

        context.Hovers.Add(BuildHover(snapshot, member, path, memberPath, provenance, editability));
    }

    public static void AnnotateElement(
        ConfiglueViewerEmissionContext context,
        ConfiglueMemberPath path,
        string elementPath,
        ConfiglueViewerRange valueRange,
        bool isSecret
    )
    {
        var snapshot = context.Snapshot;
        if (snapshot is null)
        {
            return;
        }

        // Member-level provenance only (#288). Per-element collection provenance is an
        // explicit advanced opt-in via ConfiglueMergeProvenance and is not shown by default.
        // Elements keep secret markers and inherit the parent member's editability.
        if (isSecret)
        {
            context.Decorations.Add(
                new ConfiglueViewerDecoration(
                    elementPath,
                    valueRange,
                    ConfiglueViewerDecorationKind.Secret,
                    "secret",
                    "configlue-secret"
                )
            );
        }

        var editability = snapshot.Editability(path);
        var editableText =
            editability == ConfiglueEditability.Editable ? "Yes" : $"No ({editability})";
        string markdown;
        if (isSecret)
        {
            markdown = $"### {elementPath}\n\nSecret: Yes\n\nEditable: {editableText}";
        }
        else
        {
            markdown =
                $"### {elementPath}\n\nSee the parent collection member for effective source and contributions.\n\nEditable: {editableText}";
        }

        context.Hovers.Add(
            new ConfiglueViewerHover(
                elementPath,
                elementPath,
                markdown,
                null,
                editability.ToString(),
                isSecret,
                [],
                snapshot.Sources.FirstOrDefault()?.Locator
            )
        );
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
}
