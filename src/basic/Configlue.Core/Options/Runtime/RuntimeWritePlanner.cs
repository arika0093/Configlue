using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Owns write planning for one runtime: write-plan ownership, default-source inference,
/// save-patch routing, change partitioning, and merge-aware contribution planning.
///
/// Pure planning only: this type never reads sources, never writes, and never resolves
/// the merged model. All backend I/O lives in <c>RuntimeWritePreparer</c> and
/// <c>RuntimeWriteExecutor</c>; the topology is the only runtime collaborator.
/// </summary>
internal sealed class RuntimeWritePlanner<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeSourceTopology<TFragment> _topology;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly StateWritePlan _writePlan;
    private readonly bool _defaultWriteSourceIsInferred;

    internal RuntimeWritePlanner(
        RuntimeSourceTopology<TFragment> topology,
        RuntimeDiagnosticRecorder diagnostics,
        StateWritePlan? configuredWritePlan
    )
    {
        _topology = topology;
        _diagnostics = diagnostics;
        (_writePlan, _defaultWriteSourceIsInferred) = ResolveWriteOwnership(
            topology.GetActiveSources(),
            configuredWritePlan ?? StateWritePlan.Empty
        );
    }

    internal StateWritePlan Plan => _writePlan;

    internal bool DefaultWriteSourceIsInferred => _defaultWriteSourceIsInferred;

    private static (StateWritePlan Plan, bool DefaultInferred) ResolveWriteOwnership(
        IReadOnlyList<StateSource<TFragment>> sources,
        StateWritePlan configuredWritePlan
    )
    {
        var ownedPaths = new List<(string Path, SourceId SourceId)>();
        foreach (var source in sources)
        {
            if (source.Writer is null || source.ExplicitOnly)
            {
                continue;
            }

            foreach (var path in source.OwnedPropertyPaths)
            {
                ownedPaths.Add((path, source.Id));
            }
        }

        if (ownedPaths.Count > 1 && HasOverlappingOwnershipPaths(ownedPaths))
        {
            ThrowOverlappingOwnership(ownedPaths);
        }

        var owners = new Dictionary<string, SourceId>(StringComparer.Ordinal);
        foreach (var (path, sourceId) in ownedPaths)
        {
            owners.Add(path, sourceId);
        }

        var mountedWritePlan =
            owners.Count == 0 ? StateWritePlan.Empty : new StateWritePlan(null, owners);
        var merged = mountedWritePlan.OverrideWith(configuredWritePlan);

        var defaultSourceId = configuredWritePlan.DefaultSourceId;
        var defaultInferred = false;
        if (defaultSourceId is null)
        {
            StateSource<TFragment>? singleRoot = null;
            foreach (var source in sources)
            {
                if (
                    source.Writer is null
                    || source.ExplicitOnly
                    || source.OwnedPropertyPaths.Count > 0
                )
                {
                    continue;
                }

                if (singleRoot is not null)
                {
                    throw new InvalidOperationException(
                        $"Model '{typeof(TModel)}' has multiple writable root sources ('{singleRoot.Id}' and '{source.Id}') and no default write owner. "
                            + "Configure a default write owner with model.Writes(write => write.DefaultTo(...))."
                    );
                }

                singleRoot = source;
            }

            if (singleRoot is not null)
            {
                defaultSourceId = singleRoot.Id;
                defaultInferred = true;
            }
        }
        else if (!sources.Any(source => source.Id == defaultSourceId))
        {
            throw new InvalidOperationException(
                $"The configured default write source '{defaultSourceId}' is not registered for model '{typeof(TModel)}'."
            );
        }

        return (
            merged
                .WithDefaultSourceId(defaultSourceId)
                .Bind(RuntimeModel<TModel, TFragment>.Schema),
            defaultInferred
        );

        static bool HasOverlappingOwnershipPaths(List<(string Path, SourceId SourceId)> paths)
        {
            var sorted = new string[paths.Count];
            for (var index = 0; index < paths.Count; index++)
            {
                sorted[index] = paths[index].Path;
            }

            Array.Sort(sorted, static (first, second) => CompareOwnedPathOrder(first, second));
            for (var index = 1; index < sorted.Length; index++)
            {
                if (PathsOverlap(sorted[index - 1], sorted[index]))
                {
                    return true;
                }
            }

            return false;
        }

        static int CompareOwnedPathOrder(string first, string second)
        {
            var length = Math.Min(first.Length, second.Length);
            for (var index = 0; index < length; index++)
            {
                var comparison = OwnedPathCharOrder(first[index])
                    .CompareTo(OwnedPathCharOrder(second[index]));
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return first.Length.CompareTo(second.Length);
        }

        static int OwnedPathCharOrder(char value) => value == '.' ? 0 : value + 1;

        static bool PathsOverlap(string first, string second) =>
            string.Equals(first, second, StringComparison.Ordinal)
            || IsAncestorOwnedPath(first, second)
            || IsAncestorOwnedPath(second, first);

        static bool IsAncestorOwnedPath(string ancestor, string descendant) =>
            descendant.Length > ancestor.Length
            && descendant[ancestor.Length] == '.'
            && descendant.AsSpan(0, ancestor.Length).SequenceEqual(ancestor.AsSpan());

        static void ThrowOverlappingOwnership(List<(string Path, SourceId SourceId)> paths)
        {
            var seenOwners = new Dictionary<string, SourceId>(StringComparer.Ordinal);
            foreach (var (path, sourceId) in paths)
            {
                var existingOwner = seenOwners.FirstOrDefault(owner =>
                    string.Equals(owner.Key, path, StringComparison.Ordinal)
                    || owner.Key.StartsWith(path + ".", StringComparison.Ordinal)
                    || path.StartsWith(owner.Key + ".", StringComparison.Ordinal)
                );
                if (!string.IsNullOrEmpty(existingOwner.Key))
                {
                    throw new InvalidOperationException(
                        $"Writable sources '{existingOwner.Value}' and '{sourceId}' have overlapping ownership paths '{existingOwner.Key}' and '{path}'. Configure one source as explicit-only."
                    );
                }

                seenOwners.Add(path, sourceId);
            }
        }
    }

    internal void ValidateWritePlan(StateWritePlan writePlan)
    {
        foreach (var sourceId in writePlan.PropertyRoutes.Values)
        {
            var source = _topology.FindSource(sourceId);
            if (!_topology.IsSourceActive(source.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' has been retired from this state instance."
                );
            }

            if (source.Writer is null)
            {
                throw new InvalidOperationException(
                    $"State source '{sourceId}' does not support writes."
                );
            }
        }
    }

    internal StateSource<TFragment> FindSource(SourceId sourceId) => _topology.FindSource(sourceId);

    internal bool IsSourceActive(SourceId sourceId) => _topology.IsSourceActive(sourceId);

    internal StateSource<TFragment>[] GetReversedActiveSources() =>
        _topology.GetReversedActiveSources();

    internal StateSource<TFragment>? ResolveDefaultWriteSource()
    {
        if (
            _topology.IsSingleSourceFastPath
            && _topology.FastPathWriteSource is not null
            && ReferenceEquals(_topology.GetActiveSources(), _topology.FastPathSources)
        )
        {
            // Single-file fast path (#231): the only writable root was pre-resolved at
            // construction, so ordinary saves skip the per-save topology scan.
            return _topology.FastPathWriteSource;
        }

        if (_writePlan.DefaultSourceId is not { } defaultSourceId)
        {
            return null;
        }

        var source = _topology
            .GetActiveSources()
            .FirstOrDefault(candidate => candidate.Id == defaultSourceId);
        if (source is null)
        {
            if (_topology.SourceSet.Sources.Any(candidate => candidate.Id == defaultSourceId))
            {
                throw new InvalidOperationException(
                    $"State source '{defaultSourceId}' has been retired from this state instance."
                );
            }

            throw new InvalidOperationException(
                $"State source '{defaultSourceId}' is not registered."
            );
        }

        if (source.Writer is null)
        {
            throw new InvalidOperationException(
                $"State source '{source.Id}' does not support writes."
            );
        }

        return source;
    }

    internal void ValidatePatchSchema(IConfiglueModelPatch<TModel> patch)
    {
        var modelSchema = RuntimeModel<TModel, TFragment>.Schema;
        if (
            patch.Schema.ModelType != typeof(TModel)
            || patch.Schema.Id != modelSchema.Id
            || patch.Schema.Version != modelSchema.Version
        )
        {
            throw new ArgumentException(
                $"The patch schema '{patch.Schema.Id}' does not match '{modelSchema.Id}'.",
                nameof(patch)
            );
        }
    }

    /// <summary>
    /// Routes a model save patch to per-source patches using the bound write plan.
    /// </summary>
    internal IReadOnlyDictionary<SourceId, IConfigluePatch> RouteSavePatches(
        IConfiglueModelPatch<TModel> patch,
        StateSource<TFragment>? fallbackSource
    )
    {
        var modelSchema = RuntimeModel<TModel, TFragment>.Schema;
        if (_writePlan.PropertyRoutes.Count == 0 && fallbackSource is not null)
        {
            // With a single default writable source every member routes to it, so the
            // per-member routing work can be skipped entirely.
            return new Dictionary<SourceId, IConfigluePatch>() { [fallbackSource.Id] = patch };
        }

        if (patch is IConfiglueRoutablePatch routablePatch)
        {
            return routablePatch.Route(_writePlan, _writePlan.DefaultSourceId);
        }

        if (patch is IConfiglueDynamicMemberPatch memberPatch)
        {
            return RouteDynamicMemberPatch(memberPatch, modelSchema, fallbackSource);
        }

        return new Dictionary<SourceId, IConfigluePatch>()
        {
            [
                fallbackSource?.Id
                    ?? throw new InvalidOperationException(
                        "No writable source owns this patch. Configure a root write target or an explicit write plan."
                    )
            ] = patch,
        };
    }

    private IReadOnlyDictionary<SourceId, IConfigluePatch> RouteDynamicMemberPatch(
        IConfiglueDynamicMemberPatch memberPatch,
        ConfiglueModelSchema modelSchema,
        StateSource<TFragment>? fallbackSource
    )
    {
        var routed = new Dictionary<SourceId, List<int>>();
        foreach (var member in modelSchema.Members)
        {
            var selected = memberPatch.SelectMembers([member.Id]);
            if (selected.IsEmpty)
            {
                continue;
            }

            var targetSourceId = _writePlan.ResolveSourceIdOrNull(
                ConfiglueMemberPath.Root(modelSchema).Append(member.Id)
            );
            if (targetSourceId is null)
            {
                throw new InvalidOperationException(
                    $"No writable source owns '{member.Name}'. Configure a root write target or an explicit write plan."
                );
            }
            var resolvedTargetSourceId = targetSourceId.Value;
            if (!routed.TryGetValue(resolvedTargetSourceId, out var memberIds))
            {
                memberIds = [];
                routed.Add(resolvedTargetSourceId, memberIds);
            }

            memberIds.Add(member.Id);
        }

        return routed.Count == 0
            ? new Dictionary<SourceId, IConfigluePatch>()
            {
                [
                    fallbackSource?.Id
                        ?? throw new InvalidOperationException(
                            "No writable source owns this patch. Configure a root write target or an explicit write plan."
                        )
                ] = memberPatch,
            }
            : routed.ToDictionary(
                static route => route.Key,
                route => memberPatch.SelectMembers(route.Value.ToArray()),
                EqualityComparer<SourceId>.Default
            );
    }

    internal StateSourcePatch[] CreateRoutedPatches(
        Dictionary<SourceId, IConfiglueFragment> routedChanges,
        TModel after,
        IReadOnlyList<ResolvedContribution<TFragment>> baselineContributions
    )
    {
        var patches = new List<StateSourcePatch>(routedChanges.Count);
        StateSource<TFragment>[]? reversedActiveSources = null;
        foreach (var (sourceId, sourceChanges) in routedChanges)
        {
            var plannedChanges = (TFragment)PlanMergeAwareChanges(
                RuntimeModel<TModel, TFragment>.Schema,
                sourceChanges,
                after,
                [],
                sourceId,
                baselineContributions,
                NeedsSourceOrder(RuntimeModel<TModel, TFragment>.Schema, sourceChanges)
                    ? reversedActiveSources ??= _topology.GetReversedActiveSources()
                    : null
            );
            if (!plannedChanges.IsEmpty)
            {
                patches.Add(
                    new StateSourcePatch(
                        sourceId,
                        new RuntimeFragmentChangesPatch<TModel, TFragment>(plannedChanges)
                    )
                );
            }
        }

        return patches.ToArray();
    }

    // NOTE: GetReplaceMemberPaths below builds dotted strings, but it runs only on
    // the write-conflict error path (PatchApplication mismatch diagnostics), never in
    // normal schema-bound routing. Per-member hot-path routing stays in ID space above.
    internal static List<string> GetReplaceMemberPaths(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        List<string> path
    )
    {
        var paths = new List<string>();
        foreach (var change in changes.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, change.Id, out var member))
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

    internal Dictionary<SourceId, IConfiglueFragment> PartitionRoutedChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        object afterModel,
        ConfiglueMemberPath path,
        StateWritePlan writePlan
    )
    {
        var routed = new Dictionary<SourceId, IConfiglueFragment>();
        foreach (var change in changes.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, change.Id, out var member))
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
                throw RuntimeState.NewConflict(
                    _diagnostics,
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

    internal Dictionary<SourceId, IConfiglueFragment> PartitionCompositeChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        CompositeStateSource<TFragment> composite,
        ConfiglueMemberPath path,
        StateWritePlan boundCompositePlan
    )
    {
        var routed = new Dictionary<SourceId, IConfiglueFragment>();
        foreach (var change in changes.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, change.Id, out var member))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.Id}' has no member with id {change.Id}."
                );
            }

            // Generated-ID routing: extend the compiled member path with the member ID
            // and resolve against the pre-bound composite plan. No dotted strings are
            // built per member and no names are re-resolved through Split/lookup.
            var propertyPath = path.Append(member.Id);
            if (
                member.NestedSchemaFactory is not null
                && change.Value is IConfiglueFragment nestedChanges
                && composite.HasWriteRouteBelow(propertyPath, boundCompositePlan)
            )
            {
                var nestedRouted = PartitionCompositeChanges(
                    member.NestedSchemaFactory(),
                    nestedChanges,
                    composite,
                    propertyPath,
                    boundCompositePlan
                );
                foreach (var (nestedComponentId, nestedFragment) in nestedRouted)
                {
                    var componentChanges = routed.TryGetValue(nestedComponentId, out var existing)
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
                && composite.HasWriteRouteBelow(propertyPath, boundCompositePlan)
            )
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"The edit replaces nested member '{propertyPath}' as a whole, so its more specific composite component routes cannot be applied."
                );
            }

            var targetComponentId = composite
                .ResolveWriteComponent(propertyPath, boundCompositePlan)
                .Id;
            var targetChanges = routed.TryGetValue(targetComponentId, out var current)
                ? current
                : schema.CreateEmptyFragment();
            routed[targetComponentId] = targetChanges.WithMember(member.Id, change.Value);
        }

        return routed;
    }

    private static bool NeedsSourceOrder(ConfiglueModelSchema schema, IConfiglueFragment changes)
    {
        foreach (var change in changes.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, change.Id, out var member))
            {
                continue;
            }

            if (member.MergeStrategy is not null)
            {
                return true;
            }

            if (
                member.CollectionValueFactory is not null
                && (member.MergeMode == MergeMode.Append || member.MergeMode == MergeMode.SetUnion)
            )
            {
                return true;
            }

            if (
                member.NestedSchemaFactory is not null
                && change.Value is IConfiglueFragment nested
                && NeedsSourceOrder(member.NestedSchemaFactory(), nested)
            )
            {
                return true;
            }
        }

        return false;
    }

    private IConfiglueFragment PlanMergeAwareChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        object? afterModel,
        List<string> path,
        SourceId targetSourceId,
        IReadOnlyList<ResolvedContribution<TFragment>> contributions,
        StateSource<TFragment>[]? sourceOrder
    )
    {
        foreach (var change in changes.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, change.Id, out var member))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.Id}' has no member with id {change.Id}."
                );
            }

            path.Add(member.Name);
            try
            {
                var afterValue = afterModel is null ? null : member.GetValue?.Invoke(afterModel);
                if (
                    member.NestedSchemaFactory is not null
                    && change.Value is IConfiglueFragment nestedChanges
                    && afterValue is not null
                )
                {
                    changes = changes.WithMember(
                        member.Id,
                        PlanMergeAwareChanges(
                            member.NestedSchemaFactory(),
                            nestedChanges,
                            afterValue,
                            path,
                            targetSourceId,
                            contributions,
                            sourceOrder
                        )
                    );
                    continue;
                }

                if (member.MergeStrategy is { } mergeStrategy)
                {
                    changes = PlanCustomMergeContribution(
                        changes,
                        member,
                        mergeStrategy,
                        sourceOrder!,
                        contributions,
                        targetSourceId,
                        afterValue,
                        path
                    );
                    continue;
                }

                if (
                    member.CollectionValueFactory is null
                    || afterValue is not System.Collections.IEnumerable desiredValues
                    || afterValue is string
                )
                {
                    continue;
                }

                changes = PlanCollectionContribution(
                    changes,
                    member,
                    desiredValues,
                    sourceOrder!,
                    contributions,
                    targetSourceId,
                    path
                );
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        return changes;
    }

    private IConfiglueFragment PlanCustomMergeContribution(
        IConfiglueFragment changes,
        ConfiglueMemberSchema member,
        object mergeStrategy,
        StateSource<TFragment>[] sourceOrder,
        IReadOnlyList<ResolvedContribution<TFragment>> contributions,
        SourceId targetSourceId,
        object? afterValue,
        List<string> path
    )
    {
        var strategySources = sourceOrder;
        var strategyValues = new ConfiglueMergeSourceValue[strategySources.Length];
        for (var index = 0; index < strategySources.Length; index++)
        {
            var source = strategySources[index];
            var value = Optional<object?>.Missing;
            foreach (var contribution in contributions)
            {
                if (
                    contribution.Source.Id == source.Id
                    && contribution.Result.Value is { } sourceValue
                    && RuntimeState.TryGetFragmentValue(
                        sourceValue,
                        path,
                        out var contributionValue
                    )
                )
                {
                    value = Optional<object?>.Present(contributionValue);
                    break;
                }
            }

            strategyValues[index] = new ConfiglueMergeSourceValue(source.Id, value);
        }

        if (mergeStrategy is not IConfiglueMergeContributionPlanner planner)
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"The custom merge strategy for '{member.Name}' does not support source contribution planning."
            );
        }

        if (
            !planner.TryPlanSourceContributionObject(
                strategyValues,
                targetSourceId,
                afterValue,
                out var targetContribution,
                out var reason
            )
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                reason
                    ?? $"The custom merge strategy cannot represent the edit to '{member.Name}' in source '{targetSourceId}'."
            );
        }

        return targetContribution.IsPresent
            ? changes.WithMember(member.Id, targetContribution.Value)
            : changes.WithoutMember(member.Id);
    }

    private IConfiglueFragment PlanCollectionContribution(
        IConfiglueFragment changes,
        ConfiglueMemberSchema member,
        System.Collections.IEnumerable desiredValues,
        StateSource<TFragment>[] sourceOrder,
        IReadOnlyList<ResolvedContribution<TFragment>> contributions,
        SourceId targetSourceId,
        List<string> path
    )
    {
        var desired = desiredValues.Cast<object?>().ToList();
        var valuesBySource = GetCollectionContributions(path, contributions);
        var orderedSources = sourceOrder;
        var targetIndex = Array.FindIndex(
            orderedSources,
            candidate => candidate.Id == targetSourceId
        );
        if (targetIndex < 0)
        {
            throw new InvalidOperationException(
                $"State source '{targetSourceId}' is not registered."
            );
        }

        var targetValues = member.MergeMode switch
        {
            MergeMode.Append => PlanAppendContribution(
                orderedSources,
                targetIndex,
                valuesBySource,
                desired,
                member.Name
            ),
            MergeMode.SetUnion => PlanSetUnionContribution(
                orderedSources,
                targetIndex,
                valuesBySource,
                desired,
                member
            ),
            _ => desired,
        };
        if (member.CollectionValueFactory is not { } factory)
        {
            return changes;
        }

        return changes.WithMember(member.Id, factory(targetValues));
    }

    private static Dictionary<SourceId, List<object?>> GetCollectionContributions(
        IReadOnlyList<string> path,
        IReadOnlyList<ResolvedContribution<TFragment>> contributions
    )
    {
        var valuesBySource = new Dictionary<SourceId, List<object?>>();
        foreach (var contribution in contributions)
        {
            if (
                !RuntimeState.TryGetFragmentValue(contribution.Result.Value!, path, out var value)
                || value is not System.Collections.IEnumerable values
                || value is string
            )
            {
                continue;
            }

            valuesBySource[contribution.Source.Id] = values.Cast<object?>().ToList();
        }

        return valuesBySource;
    }

    private List<object?> PlanAppendContribution(
        IReadOnlyList<StateSource<TFragment>> sourceOrder,
        int targetIndex,
        IReadOnlyDictionary<SourceId, List<object?>> valuesBySource,
        IReadOnlyList<object?> desired,
        string memberName
    )
    {
        var prefix = new List<object?>();
        for (var index = 0; index < targetIndex; index++)
        {
            if (valuesBySource.TryGetValue(sourceOrder[index].Id, out var values))
            {
                prefix.AddRange(values);
            }
        }

        var suffix = new List<object?>();
        for (var index = targetIndex + 1; index < sourceOrder.Count; index++)
        {
            if (valuesBySource.TryGetValue(sourceOrder[index].Id, out var values))
            {
                suffix.AddRange(values);
            }
        }

        if (desired.Count < prefix.Count + suffix.Count)
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"The edit to append-merged member '{memberName}' cannot be represented by source '{sourceOrder[targetIndex].Id}' while preserving other source contributions."
            );
        }

        var comparer = EqualityComparer<object?>.Default;
        var prefixMatches = true;
        for (var index = 0; index < prefix.Count; index++)
        {
            if (!comparer.Equals(desired[index], prefix[index]))
            {
                prefixMatches = false;
                break;
            }
        }

        var suffixMatches = true;
        var suffixOffset = desired.Count - suffix.Count;
        for (var index = 0; index < suffix.Count; index++)
        {
            if (!comparer.Equals(desired[suffixOffset + index], suffix[index]))
            {
                suffixMatches = false;
                break;
            }
        }

        if (!prefixMatches || !suffixMatches)
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"The edit to append-merged member '{memberName}' cannot be represented by source '{sourceOrder[targetIndex].Id}' while preserving other source contributions."
            );
        }

        var contributionCount = desired.Count - prefix.Count - suffix.Count;
        var targetValues = new List<object?>(contributionCount);
        for (var index = 0; index < contributionCount; index++)
        {
            targetValues.Add(desired[prefix.Count + index]);
        }

        return targetValues;
    }

    private List<object?> PlanSetUnionContribution(
        IReadOnlyList<StateSource<TFragment>> sourceOrder,
        int targetIndex,
        IReadOnlyDictionary<SourceId, List<object?>> valuesBySource,
        IReadOnlyList<object?> desired,
        ConfiglueMemberSchema member
    )
    {
        var comparer = EqualityComparer<object?>.Default;
        var otherValues = new HashSet<object?>(comparer);
        for (var index = 0; index < sourceOrder.Count; index++)
        {
            if (
                index == targetIndex
                || !valuesBySource.TryGetValue(sourceOrder[index].Id, out var otherSourceValues)
            )
            {
                continue;
            }

            for (var valueIndex = 0; valueIndex < otherSourceValues.Count; valueIndex++)
            {
                otherValues.Add(otherSourceValues[valueIndex]);
            }
        }

        var desiredSet = new HashSet<object?>(comparer);
        for (var index = 0; index < desired.Count; index++)
        {
            desiredSet.Add(desired[index]);
        }

        if (otherValues.Any(value => !desiredSet.Contains(value)))
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"The edit to set-union member '{member.Name}' removes a value contributed by another source."
            );
        }

        var targetValues = new List<object?>();
        for (var index = 0; index < desired.Count; index++)
        {
            if (!otherValues.Contains(desired[index]))
            {
                targetValues.Add(desired[index]);
            }
        }

        var merged = new List<object?>();
        var mergedSet = new HashSet<object?>(comparer);
        for (var index = 0; index < sourceOrder.Count; index++)
        {
            IEnumerable<object?> values;
            if (index == targetIndex)
            {
                values = targetValues;
            }
            else if (valuesBySource.TryGetValue(sourceOrder[index].Id, out var sourceValues))
            {
                values = sourceValues;
            }
            else
            {
                values = [];
            }

            merged.AddRange(values.Where(value => mergedSet.Add(value)));
        }

        var isSet =
            member.ValueType.IsGenericType
            && (
                member.ValueType.GetGenericTypeDefinition() == typeof(ISet<>)
                || member.ValueType.GetGenericTypeDefinition().FullName
                    == "System.Collections.Generic.IReadOnlySet`1"
                || member.ValueType.GetGenericTypeDefinition() == typeof(HashSet<>)
            );
        var matchesDesired = isSet
            ? merged.Count == desiredSet.Count && merged.All(desiredSet.Contains)
            : merged.SequenceEqual(desired);
        if (!matchesDesired)
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"The edit to set-union member '{member.Name}' cannot be represented by source '{sourceOrder[targetIndex].Id}' while preserving other source contributions."
            );
        }

        return targetValues;
    }
}

/// <summary>
/// Patch over a pre-partitioned fragment. Produced by merge-aware planning and consumed
/// by write preparation (including composite-source routing).
/// </summary>
internal sealed class RuntimeFragmentChangesPatch<TModel, TFragment>(TFragment changes)
    : IConfiglueDynamicMemberPatch
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    public TFragment Changes => changes;

    public ConfiglueModelSchema Schema => RuntimeModel<TModel, TFragment>.Schema;

    public bool IsEmpty => changes.IsEmpty;

    public IConfiglueFragment Apply(IConfiglueFragment fragment)
    {
        if (fragment is not TFragment sourceFragment)
        {
            throw new ArgumentException(
                "The patch received an incompatible fragment.",
                nameof(fragment)
            );
        }

        return sourceFragment.ApplyChanges(changes);
    }

    public IConfigluePatch SelectMembers(ReadOnlySpan<int> memberIds)
    {
        var selected = RuntimeModel<TModel, TFragment>.EmptyFragment;
        foreach (var member in changes.EnumeratePresentMembersFast())
        {
            for (var index = 0; index < memberIds.Length; index++)
            {
                if (memberIds[index] == member.Id)
                {
                    selected = (TFragment)selected.WithMember(member.Id, member.Value);
                    break;
                }
            }
        }

        return new RuntimeFragmentChangesPatch<TModel, TFragment>(selected);
    }
}
