using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace Configlue;

public sealed partial class ConfiglueOptions<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private async ValueTask<StateWriteResult> WriteChangesToSourceAsync(
        StateSource<TFragment> source,
        TModel before,
        TModel after,
        string? expectedRevision,
        IReadOnlyList<ResolvedContribution> baselineContributions,
        StateRevisionVector? expectedBaselineRevisions,
        CancellationToken cancellationToken
    )
    {
        using var operation = EnterOperation();
        after = CloneModel(after);
        Validate(after);
        var requestedChanges = TModel.Diff(before, after);
        if (requestedChanges.IsEmpty)
        {
            return new StateWriteResult(expectedRevision);
        }

        var searchCandidates = _writeRoute.SourceId is null;
        var candidates = searchCandidates
            ? GetActiveSources().Where(static candidate => candidate.Writer is not null).ToArray()
            : [source];
        var needsSourceOrder = NeedsSourceOrder(TModel.ConfiglueSchema, requestedChanges);
        StateSource<TFragment>[]? reversedActiveSources = null;
        string? lastFailure = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidateExpectedRevision = expectedRevision;
            if (
                !string.Equals(candidate.Id, source.Id, StringComparison.Ordinal)
                && (
                    expectedBaselineRevisions is null
                    || !expectedBaselineRevisions.TryGetRevision(
                        candidate.Id,
                        out candidateExpectedRevision
                    )
                )
            )
            {
                throw LogConflict(
                    $"The edit baseline has no revision for candidate source '{candidate.Id}'."
                );
            }

            var current = await candidate.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (
                !string.Equals(
                    current.Revision,
                    candidateExpectedRevision,
                    StringComparison.Ordinal
                )
            )
            {
                throw LogConflict(
                    $"State source '{candidate.Id}' changed after the configuration edit began."
                );
            }

            if (current.Status == StateReadStatus.Unavailable)
            {
                lastFailure = $"Candidate source '{candidate.Id}' is unavailable.";
                if (searchCandidates)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Cannot safely update configuration because source '{candidate.Id}' is unavailable."
                );
            }

            var sourceFragment =
                current.Status == StateReadStatus.Success
                    ? current.Value
                        ?? throw new InvalidOperationException(
                            $"State source '{candidate.Id}' returned a null configuration fragment."
                        )
                    : TFragment.Empty;
            if (current.Schema is { } schema)
            {
                sourceFragment = await MigrateAsync(sourceFragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            TFragment plannedChanges;
            try
            {
                plannedChanges = (TFragment)PlanMergeAwareChanges(
                    TModel.ConfiglueSchema,
                    requestedChanges,
                    after,
                    [],
                    candidate.Id,
                    baselineContributions,
                    needsSourceOrder ? reversedActiveSources ??= GetReversedActiveSources() : null
                );
            }
            catch (StateConflictException exception) when (searchCandidates)
            {
                lastFailure = exception.Message;
                continue;
            }

            var updated = sourceFragment.ApplyChanges(plannedChanges);
            var proposed = await ReadCoreAsync(
                    new Dictionary<string, StateReadResult<TFragment>>(StringComparer.Ordinal)
                    {
                        [candidate.Id] = StateReadResult<TFragment>.Success(
                            updated,
                            current.Revision,
                            TModel.ConfiglueSchema.ToMetadata()
                        ),
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (proposed.Status != StateReadStatus.Success)
            {
                lastFailure = $"The proposal resolved to {proposed.Status}.";
                if (searchCandidates)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"The edited configuration could not be resolved: {proposed.Status}."
                );
            }

            if (!TModel.Diff(proposed.Value!, after).IsEmpty)
            {
                lastFailure =
                    $"Candidate source '{candidate.Id}' cannot realize the requested edit while preserving higher-priority contributions.";
                if (searchCandidates)
                {
                    continue;
                }

                throw LogConflict(lastFailure);
            }

            Validate(proposed.Value!);
            var latest = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (
                latest.Status != StateReadStatus.Success
                || !HaveSameRevisions(expectedBaselineRevisions, latest.Revisions)
            )
            {
                throw LogConflict(
                    "A state source changed before the configuration edit could be written."
                );
            }

            return await WriteStateAsync(
                    candidate,
                    candidate.Writer!,
                    new StateWriteRequest<TFragment>(
                        updated,
                        current.Revision,
                        CheckRevision: true
                    ),
                    "save",
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        throw LogConflict(
            lastFailure is null
                ? "No writable source candidate could realize the requested edit."
                : $"No writable source candidate could realize the requested edit. {lastFailure}"
        );
    }

    private static bool NeedsSourceOrder(ConfiglueModelSchema schema, IConfiglueFragment changes)
    {
        foreach (var change in changes.EnumeratePresentMembers())
        {
            if (!TryGetMember(schema, change.Id, out var member))
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
        string targetSourceId,
        IReadOnlyList<ResolvedContribution> contributions,
        StateSource<TFragment>[]? sourceOrder
    )
    {
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
                    var strategySources = sourceOrder!;
                    var strategyValues = new ConfiglueMergeSourceValue[strategySources.Length];
                    for (var index = 0; index < strategySources.Length; index++)
                    {
                        var source = strategySources[index];
                        var value = Optional<object?>.Missing;
                        foreach (var contribution in contributions)
                        {
                            if (
                                string.Equals(
                                    contribution.Source.Id,
                                    source.Id,
                                    StringComparison.Ordinal
                                )
                                && contribution.Result.Value is { } sourceValue
                                && TryGetFragmentValue(sourceValue, path, out var contributionValue)
                            )
                            {
                                value = Optional<object?>.Present(contributionValue);
                                break;
                            }
                        }

                        strategyValues[index] = new ConfiglueMergeSourceValue(source.Id, value);
                    }

                    if (
                        !mergeStrategy.TryPlanSourceContribution(
                            strategyValues,
                            targetSourceId,
                            afterValue,
                            out var targetContribution,
                            out var reason
                        )
                    )
                    {
                        throw LogConflict(
                            reason
                                ?? $"The custom merge strategy cannot represent the edit to '{member.Name}' in source '{targetSourceId}'."
                        );
                    }

                    changes = targetContribution.IsPresent
                        ? changes.WithMember(member.Id, targetContribution.Value)
                        : changes.WithoutMember(member.Id);
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

                var desired = desiredValues.Cast<object?>().ToList();
                var valuesBySource = GetCollectionContributions(path, contributions);
                var orderedSources = sourceOrder!;
                var targetIndex = Array.FindIndex(
                    orderedSources,
                    candidate =>
                        string.Equals(candidate.Id, targetSourceId, StringComparison.Ordinal)
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
                changes = changes.WithMember(
                    member.Id,
                    member.CollectionValueFactory(targetValues)
                );
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        return changes;
    }

    private static Dictionary<string, List<object?>> GetCollectionContributions(
        IReadOnlyList<string> path,
        IReadOnlyList<ResolvedContribution> contributions
    )
    {
        var valuesBySource = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        foreach (var contribution in contributions)
        {
            if (
                !TryGetFragmentValue(contribution.Result.Value!, path, out var value)
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
        IReadOnlyDictionary<string, List<object?>> valuesBySource,
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
            throw LogConflict(
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
            throw LogConflict(
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
        IReadOnlyDictionary<string, List<object?>> valuesBySource,
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
            throw LogConflict(
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
                || member.ValueType.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)
                || member.ValueType.GetGenericTypeDefinition() == typeof(HashSet<>)
            );
        var matchesDesired = isSet
            ? merged.Count == desiredSet.Count && merged.All(desiredSet.Contains)
            : merged.SequenceEqual(desired);
        if (!matchesDesired)
        {
            throw LogConflict(
                $"The edit to set-union member '{member.Name}' cannot be represented by source '{sourceOrder[targetIndex].Id}' while preserving other source contributions."
            );
        }

        return targetValues;
    }
}
