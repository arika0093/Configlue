using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Configlue.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private async ValueTask<StateWriteReceipt> WriteChangesToSourcesAsync(
        TModel before,
        TModel after,
        StateRevisionVector? expectedBaselineRevisions,
        IReadOnlyList<ResolvedContribution> baselineContributions,
        StateWritePlan writePlan,
        CancellationToken cancellationToken
    )
    {
        using var operation = EnterOperation();
        after = CloneModel(after);
        Validate(after);
        var changes = Diff(before, after);
        if (changes.IsEmpty)
        {
            return StateWriteReceipt.Empty;
        }

        var routedChanges = PartitionRoutedChanges(
            ModelSchema,
            changes,
            after,
            ConfiglueMemberPath.Root(ModelSchema),
            writePlan
        );
        var patches = CreateRoutedPatches(routedChanges, after, baselineContributions);
        if (patches.Length == 0)
        {
            return StateWriteReceipt.Empty;
        }

        return await ApplyPatchesCoreAsync(
                patches,
                expectedBaselineRevisions,
                after,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private StateSourcePatch[] CreateRoutedPatches(
        Dictionary<SourceId, IConfiglueFragment> routedChanges,
        TModel after,
        IReadOnlyList<ResolvedContribution> baselineContributions
    )
    {
        var patches = new List<StateSourcePatch>(routedChanges.Count);
        StateSource<TFragment>[]? reversedActiveSources = null;
        foreach (var (sourceId, sourceChanges) in routedChanges)
        {
            var plannedChanges = (TFragment)PlanMergeAwareChanges(
                ModelSchema,
                sourceChanges,
                after,
                [],
                sourceId,
                baselineContributions,
                NeedsSourceOrder(ModelSchema, sourceChanges)
                    ? reversedActiveSources ??= GetReversedActiveSources()
                    : null
            );
            if (!plannedChanges.IsEmpty)
            {
                patches.Add(
                    new StateSourcePatch(sourceId, new FragmentChangesPatch(plannedChanges))
                );
            }
        }

        return patches.ToArray();
    }

    private static List<string> GetChangedPropertyPaths(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        List<string> path
    )
    {
        var paths = new List<string>();
        foreach (var change in changes.EnumeratePresentMembers())
        {
            if (!TryGetMember(schema, change.Id, out var member))
            {
                continue;
            }

            path.Add(member.Name);
            if (member.NestedSchemaFactory is not null && change.Value is IConfiglueFragment nested)
            {
                paths.AddRange(GetChangedPropertyPaths(member.NestedSchemaFactory(), nested, path));
            }
            else
            {
                paths.Add(string.Join(".", path));
            }

            path.RemoveAt(path.Count - 1);
        }

        return paths;
    }

    private static List<string> GetReplaceMemberPaths(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        List<string> path
    )
    {
        var paths = new List<string>();
        foreach (var change in changes.EnumeratePresentMembers())
        {
            if (!TryGetMember(schema, change.Id, out var member))
            {
                continue;
            }

            path.Add(member.Name);
            if (
                member.MergeMode == MergeMode.Deep
                && member.NestedSchemaFactory is not null
                && change.Value is IConfiglueFragment nestedChanges
            )
            {
                paths.AddRange(
                    GetReplaceMemberPaths(member.NestedSchemaFactory(), nestedChanges, path)
                );
            }
            else if (member.MergeMode == MergeMode.Replace)
            {
                paths.Add(string.Join(".", path));
            }

            path.RemoveAt(path.Count - 1);
        }

        return paths;
    }

    private Dictionary<SourceId, IConfiglueFragment> PartitionRoutedChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        object afterModel,
        ConfiglueMemberPath path,
        StateWritePlan writePlan
    )
    {
        var routed = new Dictionary<SourceId, IConfiglueFragment>();
        foreach (var change in changes.EnumeratePresentMembers())
        {
            if (!TryGetMember(schema, change.Id, out var member))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.Id}' has no member with id {change.Id}."
                );
            }

            var propertyPath = path.Append(member.Id);
            var afterValue = member.GetValue?.Invoke(afterModel);
            if (
                member.NestedSchemaFactory is not null
                && change.Value is IConfiglueFragment nestedChanges
                && afterValue is not null
            )
            {
                var nestedRouted = PartitionRoutedChanges(
                    member.NestedSchemaFactory(),
                    nestedChanges,
                    afterValue,
                    propertyPath,
                    writePlan
                );
                foreach (var (sourceId, nestedFragment) in nestedRouted)
                {
                    var sourceFragment = routed.TryGetValue(sourceId, out var current)
                        ? current
                        : schema.CreateEmptyFragment();
                    routed[sourceId] = sourceFragment.WithMember(member.Id, nestedFragment);
                }

                continue;
            }

            if (
                member.NestedSchemaFactory is not null
                && afterValue is null
                && writePlan.HasRouteBelow(propertyPath)
            )
            {
                throw LogConflict(
                    $"The edit replaces nested member '{propertyPath}' with null, so its more specific source routes cannot be applied."
                );
            }

            var targetSourceId =
                writePlan.ResolveSourceIdOrNull(propertyPath)
                ?? throw new InvalidOperationException(
                    $"No writable source owns '{propertyPath}'. Configure a default write owner or an explicit write route."
                );
            var targetFragment = routed.TryGetValue(targetSourceId, out var existing)
                ? existing
                : schema.CreateEmptyFragment();
            routed[targetSourceId] = targetFragment.WithMember(member.Id, change.Value);
        }

        return routed;
    }

    private Dictionary<SourceId, IConfiglueFragment> PartitionCompositeChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        CompositeStateSource<TFragment> composite,
        List<string> path
    )
    {
        var routed = new Dictionary<SourceId, IConfiglueFragment>();
        foreach (var change in changes.EnumeratePresentMembers().ToArray())
        {
            if (!TryGetMember(schema, change.Id, out var member))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.Id}' has no member with id {change.Id}."
                );
            }

            path.Add(member.Name);
            try
            {
                var propertyPath = string.Join(".", path);
                if (
                    member.NestedSchemaFactory is not null
                    && change.Value is IConfiglueFragment nestedChanges
                    && composite.HasWriteRouteBelow(propertyPath)
                )
                {
                    var nestedRouted = PartitionCompositeChanges(
                        member.NestedSchemaFactory(),
                        nestedChanges,
                        composite,
                        path
                    );
                    foreach (var (nestedComponentId, nestedFragment) in nestedRouted)
                    {
                        var componentChanges = routed.TryGetValue(
                            nestedComponentId,
                            out var existing
                        )
                            ? existing
                            : schema.CreateEmptyFragment();
                        routed[nestedComponentId] = componentChanges.WithMember(
                            member.Id,
                            nestedFragment
                        );
                    }

                    continue;
                }

                if (
                    member.NestedSchemaFactory is not null
                    && composite.HasWriteRouteBelow(propertyPath)
                )
                {
                    throw LogConflict(
                        $"The edit replaces nested member '{propertyPath}' as a whole, so its more specific composite component routes cannot be applied."
                    );
                }

                var targetComponentId = composite.ResolveWriteComponent(propertyPath).Id;
                var targetChanges = routed.TryGetValue(targetComponentId, out var current)
                    ? current
                    : schema.CreateEmptyFragment();
                routed[targetComponentId] = targetChanges.WithMember(member.Id, change.Value);
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        return routed;
    }

    private static bool IsValidMemberPath(
        ConfiglueModelSchema schema,
        string[] segments,
        int index = 0
    )
    {
        if (!TryGetMemberByName(schema, segments[index], out var member))
        {
            return false;
        }

        if (index == segments.Length - 1)
        {
            return true;
        }

        return member.NestedSchemaFactory is not null
            && IsValidMemberPath(member.NestedSchemaFactory(), segments, index + 1);
    }
}
