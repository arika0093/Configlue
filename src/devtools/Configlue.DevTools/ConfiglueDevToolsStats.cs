using System.Text.Json;
using Configlue.CompilerServices;
using Configlue.State;

namespace Configlue.DevTools;

/// <summary>
/// Computes inexpensive value/provenance statistics from one details snapshot.
/// </summary>
/// <remarks>
/// <para>
/// Internal to the DevTools package. Consumes only the existing details-snapshot
/// transport behind generated <c>GetDetailsAsync()</c>; no new inspection API.
/// Emits counts and metadata only, never effective or contributed values.
/// </para>
/// <para>
/// Leaf rule: a leaf is one scalar member or one whole collection member.
/// Nested objects are expanded recursively and never counted themselves.
/// Null nested objects still contribute their schema leaves, so the count is
/// stable for a schema version and never misleading for nested/collection shapes.
/// Collections count as a single leaf; per-element provenance is not expanded.
/// Model defaults own leaves with no configured contribution but are excluded
/// from shadowed/missing tallies so those counts describe real source overlap.
/// </para>
/// </remarks>
internal static class ConfiglueDevToolsStats
{
    /// <summary>Documents what counts as a leaf in statistics payloads.</summary>
    public const string LeafRule =
        "leaf = scalar member or one whole collection; nested objects expand and are never counted";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Computes statistics JSON for one consistent details snapshot.</summary>
    public static string ComputeStatsJson(ConfiglueDetailsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var leaves = CollectLeaves(snapshot.Schema);
        var sourceCount = snapshot.Sources.Count;
        var ownership = new int[sourceCount];
        var leafRows = new List<object>(leaves.Count);

        var editable = 0;
        var readOnly = 0;
        var shadowedEdit = 0;
        var noWriteTarget = 0;
        var secretLeaves = 0;
        var leavesWithShadowed = 0;
        var shadowedContributions = 0;
        var presentContributions = 0;
        var missingContributions = 0;
        var unavailableContributions = 0;
        var invalidContributions = 0;

        foreach (var path in leaves)
        {
            var isSecret = path.IsSecret();
            if (isSecret)
            {
                secretLeaves++;
            }

            var editability = snapshot.Editability(path);
            switch (editability)
            {
                case ConfiglueEditability.Editable:
                    editable++;
                    break;
                case ConfiglueEditability.ReadOnly:
                    readOnly++;
                    break;
                case ConfiglueEditability.Shadowed:
                    shadowedEdit++;
                    break;
                default:
                    noWriteTarget++;
                    break;
            }

            int? effectiveIndex = null;
            var presentCount = 0;
            for (var index = 0; index < sourceCount; index++)
            {
                var fragment = snapshot.SourceFragments[index];
                object? raw;
                var present = fragment is not null && path.TryGetFragmentValue(fragment, out raw);
                var isDefaults = string.Equals(
                    snapshot.Sources[index].Kind,
                    "model-defaults",
                    StringComparison.Ordinal
                );
                if (present)
                {
                    if (effectiveIndex is null)
                    {
                        effectiveIndex = index;
                    }

                    if (!isDefaults)
                    {
                        presentCount++;
                        presentContributions++;
                        if (presentCount > 1)
                        {
                            shadowedContributions++;
                        }
                    }
                }
                else if (!isDefaults)
                {
                    switch (MapStatus(snapshot.SourceStatuses[index]))
                    {
                        case Configlue.ConfigSourceValueState.Missing:
                            missingContributions++;
                            break;
                        case Configlue.ConfigSourceValueState.Unavailable:
                            unavailableContributions++;
                            break;
                        case Configlue.ConfigSourceValueState.Invalid:
                            invalidContributions++;
                            break;
                        default:
                            missingContributions++;
                            break;
                    }
                }
            }

            var hasShadowed = presentCount > 1;
            if (hasShadowed)
            {
                leavesWithShadowed++;
            }

            if (effectiveIndex is not null)
            {
                ownership[effectiveIndex.Value]++;
            }

            string? effectiveSourceKey = null;
            string? effectiveSourceDisplay = null;
            if (effectiveIndex is not null)
            {
                effectiveSourceKey = snapshot.Sources[effectiveIndex.Value].Key;
                effectiveSourceDisplay = snapshot.Sources[effectiveIndex.Value].DisplayName;
            }

            leafRows.Add(
                new
                {
                    path = path.ToString(),
                    effectiveSource = effectiveSourceDisplay,
                    effectiveSourceKey,
                    editability = editability.ToString(),
                    isSecret,
                    presentSources = presentCount,
                    isShadowedLeaf = hasShadowed,
                }
            );
        }

        var ownershipRows = new List<object>(sourceCount);
        for (var index = 0; index < sourceCount; index++)
        {
            var source = snapshot.Sources[index];
            ownershipRows.Add(
                new
                {
                    key = source.Key,
                    kind = source.Kind,
                    displayName = source.DisplayName,
                    canWrite = source.CanWrite,
                    canWatch = source.CanWatch,
                    effectiveLeaves = ownership[index],
                }
            );
        }

        var payload = new
        {
            leafRule = LeafRule,
            totalLeaves = leaves.Count,
            editableLeaves = editable,
            readOnlyLeaves = readOnly,
            shadowedLeaves = shadowedEdit,
            noWriteTargetLeaves = noWriteTarget,
            secretLeaves,
            leavesWithShadowedContributions = leavesWithShadowed,
            shadowedContributions,
            presentContributions,
            missingContributions,
            unavailableContributions,
            invalidContributions,
            ownership = ownershipRows,
            leaves = leafRows,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static List<ConfiglueMemberPath> CollectLeaves(ConfiglueModelSchema schema)
    {
        var result = new List<ConfiglueMemberPath>();
        CollectInto(schema, ConfiglueMemberPath.Root(schema), result);
        return result;
    }

    private static void CollectInto(
        ConfiglueModelSchema schema,
        ConfiglueMemberPath prefix,
        List<ConfiglueMemberPath> result
    )
    {
        foreach (var member in schema.Members)
        {
            if (member.IsDefault)
            {
                continue;
            }

            var path = prefix.Append(member.Id);
            if (member.NestedSchemaFactory is not null)
            {
                var nested = member.NestedSchemaFactory();
                CollectInto(nested, path, result);
                continue;
            }

            result.Add(path);
        }
    }

    private static Configlue.ConfigSourceValueState MapStatus(StateReadStatus status) =>
        status switch
        {
            StateReadStatus.Unavailable => Configlue.ConfigSourceValueState.Unavailable,
            StateReadStatus.InvalidPayload => Configlue.ConfigSourceValueState.Invalid,
            _ => Configlue.ConfigSourceValueState.Missing,
        };
}
