using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace Configlue;

public sealed partial class ConfiglueOptions<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private async ValueTask<StateWriteResult> WriteChangesToSourcesAsync(
        StateSource<TFragment> fallbackSource,
        TModel before,
        TModel after,
        string? expectedFallbackRevision,
        StateRevisionVector? expectedBaselineRevisions,
        IReadOnlyList<ResolvedContribution> baselineContributions,
        StateWritePlan writePlan,
        CancellationToken cancellationToken
    )
    {
        using var operation = EnterOperation();
        after = CloneModel(after);
        Validate(after);
        var changes = TModel.Diff(before, after);
        if (changes.IsEmpty)
        {
            return new StateWriteResult(expectedFallbackRevision);
        }

        var canSearchFallbackCandidates = _writeRoute.SourceId is null;
        var routingFallbackSourceId =
            fallbackSource.OwnedPropertyPaths.Count == 0 && !fallbackSource.ExplicitOnly
                ? fallbackSource.Id
                : "__configlue_missing_write_owner__";
        var initialRouting = PartitionRoutedChanges(
            TModel.ConfiglueSchema,
            changes,
            after,
            [],
            routingFallbackSourceId,
            writePlan
        );
        var hasUnroutedChanges = initialRouting.ContainsKey(routingFallbackSourceId);
        var fallbackCandidateIds =
            canSearchFallbackCandidates && hasUnroutedChanges
                ? GetActiveSources()
                    .Where(static candidate =>
                        candidate.Writer is not null
                        && !candidate.ExplicitOnly
                        && candidate.OwnedPropertyPaths.Count == 0
                    )
                    .Select(static candidate => candidate.Id)
                    .ToArray()
                : [fallbackSource.Id];
        if (hasUnroutedChanges && fallbackCandidateIds.Length == 0)
        {
            throw new InvalidOperationException(
                "No writable root source owns one or more edited properties. Configure a root write target or an explicit write plan."
            );
        }
        StateSourcePatch[]? patches = null;
        var selectedFallbackSourceId = fallbackSource.Id;
        string? lastFailure = null;
        foreach (var candidateId in fallbackCandidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var routedChanges = string.Equals(
                candidateId,
                fallbackSource.Id,
                StringComparison.Ordinal
            )
                ? initialRouting
                : PartitionRoutedChanges(
                    TModel.ConfiglueSchema,
                    changes,
                    after,
                    [],
                    candidateId,
                    writePlan
                );

            StateSourcePatch[] candidatePatches;
            try
            {
                candidatePatches = CreateRoutedPatches(routedChanges, after, baselineContributions);
            }
            catch (StateConflictException exception) when (canSearchFallbackCandidates)
            {
                lastFailure = exception.Message;
                continue;
            }

            if (candidatePatches.Length == 0)
            {
                return new StateWriteResult(expectedFallbackRevision);
            }

            if (
                canSearchFallbackCandidates
                && hasUnroutedChanges
                && await CanRealizePatchBatchAsync(
                        candidatePatches,
                        expectedBaselineRevisions,
                        after,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                    is { } realizationFailure
            )
            {
                lastFailure =
                    $"Candidate source '{candidateId}' cannot realize the requested edit. {realizationFailure}";
                continue;
            }

            patches = candidatePatches;
            selectedFallbackSourceId = candidateId;
            break;
        }

        if (patches is null)
        {
            throw LogConflict(
                lastFailure is null
                    ? "No writable source candidate could realize the routed configuration edit."
                    : $"No writable source candidate could realize the routed configuration edit. {lastFailure}"
            );
        }

        var result = await ApplyPatchesCoreAsync(
                patches,
                expectedBaselineRevisions,
                after,
                cancellationToken
            )
            .ConfigureAwait(false);
        var fallbackResult = result.Sources.FirstOrDefault(source =>
            string.Equals(source.SourceId, selectedFallbackSourceId, StringComparison.Ordinal)
        );
        var revision = fallbackResult.SourceId is not null
            ? fallbackResult.Revision
            : result.Sources[0].Revision;
        return new StateWriteResult(revision) { MultiWriteResult = result };
    }

    private StateSourcePatch[] CreateRoutedPatches(
        Dictionary<string, IConfiglueFragment> routedChanges,
        TModel after,
        IReadOnlyList<ResolvedContribution> baselineContributions
    )
    {
        var patches = new List<StateSourcePatch>(routedChanges.Count);
        StateSource<TFragment>[]? reversedActiveSources = null;
        foreach (var (sourceId, sourceChanges) in routedChanges)
        {
            var plannedChanges = (TFragment)PlanMergeAwareChanges(
                TModel.ConfiglueSchema,
                sourceChanges,
                after,
                [],
                sourceId,
                baselineContributions,
                NeedsSourceOrder(TModel.ConfiglueSchema, sourceChanges)
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

    private async ValueTask<string?> CanRealizePatchBatchAsync(
        StateSourcePatch[] patchRequests,
        StateRevisionVector? expectedBaselineRevisions,
        TModel expectedResolvedModel,
        CancellationToken cancellationToken
    )
    {
        var baseline = await ResolveCoreAsync(null, cancellationToken).ConfigureAwait(false);
        if (baseline.Result.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read before planning writes: {baseline.Result.Status}."
            );
        }

        if (
            expectedBaselineRevisions is not null
            && !HaveSameRevisions(expectedBaselineRevisions, baseline.Result.Revisions)
        )
        {
            throw LogConflict("A state source changed after the configuration edit began.");
        }

        var replacements = new Dictionary<string, StateReadResult<TFragment>>(
            StringComparer.Ordinal
        );
        foreach (var patchRequest in patchRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = FindSource(patchRequest.SourceId);
            if (!IsSourceActive(source.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{source.Id}' has been retired from this options instance."
                );
            }

            var current = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                return $"Source '{source.Id}' is unavailable.";
            }

            if (
                baseline.Result.Revisions is null
                || !baseline.Result.Revisions.TryGetRevision(source.Id, out var baselineRevision)
                || !string.Equals(current.Revision, baselineRevision, StringComparison.Ordinal)
            )
            {
                throw LogConflict(
                    $"State source '{source.Id}' changed while the write plan was being evaluated."
                );
            }

            var sourceFragment = current.Status switch
            {
                StateReadStatus.NotFound => TFragment.Empty,
                StateReadStatus.Success => current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{source.Id}' returned a null configuration fragment."
                    ),
                _ => throw new InvalidOperationException(
                    $"Source '{source.Id}' could not be planned: {current.Status}."
                ),
            };
            if (current.Schema is { } schema)
            {
                sourceFragment = await MigrateAsync(sourceFragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (patchRequest.Patch.Apply(sourceFragment) is not TFragment patchedFragment)
            {
                throw new InvalidOperationException(
                    $"The patch for source '{source.Id}' returned an incompatible fragment."
                );
            }

            replacements.Add(
                source.Id,
                StateReadResult<TFragment>.Success(
                    patchedFragment,
                    current.Revision,
                    TModel.ConfiglueSchema.ToMetadata()
                )
            );
        }

        var proposed = await ResolveCoreAsync(
                replacements,
                cancellationToken,
                captureContributions: true
            )
            .ConfigureAwait(false);
        if (proposed.Result.Status != StateReadStatus.Success)
        {
            return $"The proposal resolved to {proposed.Result.Status}.";
        }

        if (!HaveSameRevisions(baseline.Result.Revisions, proposed.Result.Revisions))
        {
            throw LogConflict("A state source changed while the write plan was being evaluated.");
        }

        var mismatch = TModel.Diff(proposed.Result.Value!, expectedResolvedModel);
        if (!mismatch.IsEmpty)
        {
            var paths = GetReplaceMemberPaths(TModel.ConfiglueSchema, mismatch, []);
            var readonlySources = proposed
                .Contributions.Where(static contribution => contribution.Source.Writer is null)
                .Select(static contribution => contribution.Source.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var details = paths.Count == 0 ? string.Empty : $" for '{string.Join("', '", paths)}'";
            var shadowing =
                readonlySources.Length == 0
                    ? string.Empty
                    : $" Read-only source(s) contributing to the resolved state: '{string.Join("', '", readonlySources)}'. A higher-priority contribution may shadow the write.";
            return $"The requested edit could not be realized{details}.{shadowing}";
        }

        Validate(proposed.Result.Value!);
        return null;
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
                paths.Add(string.Join('.', path));
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
                paths.Add(string.Join('.', path));
            }

            path.RemoveAt(path.Count - 1);
        }

        return paths;
    }

    private Dictionary<string, IConfiglueFragment> PartitionRoutedChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        object afterModel,
        List<string> path,
        string fallbackSourceId,
        StateWritePlan writePlan
    )
    {
        var routed = new Dictionary<string, IConfiglueFragment>(StringComparer.Ordinal);
        foreach (var change in changes.EnumeratePresentMembers())
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
                var propertyPath = string.Join('.', path);
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
                        path,
                        fallbackSourceId,
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

                var targetSourceId = writePlan.ResolveSourceId(propertyPath, fallbackSourceId);
                var targetFragment = routed.TryGetValue(targetSourceId, out var existing)
                    ? existing
                    : schema.CreateEmptyFragment();
                routed[targetSourceId] = targetFragment.WithMember(member.Id, change.Value);
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        return routed;
    }

    private Dictionary<string, IConfiglueFragment> PartitionCompositeChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        CompositeStateSource<TFragment> composite,
        List<string> path
    )
    {
        var routed = new Dictionary<string, IConfiglueFragment>(StringComparer.Ordinal);
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
                var propertyPath = string.Join('.', path);
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
