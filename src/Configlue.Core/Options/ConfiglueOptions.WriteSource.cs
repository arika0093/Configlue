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
                    baselineContributions
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

    private IConfiglueFragment PlanMergeAwareChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        object? afterModel,
        List<string> path,
        string targetSourceId,
        IReadOnlyList<ResolvedContribution> contributions
    )
    {
        foreach (var change in changes.EnumeratePresentMembers().ToArray())
        {
            var member = schema.Members.FirstOrDefault(candidate => candidate.Id == change.Id);
            if (string.IsNullOrEmpty(member.Name))
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
                            contributions
                        )
                    );
                    continue;
                }

                if (member.MergeStrategy is { } mergeStrategy)
                {
                    var strategySourceOrder = GetActiveSources().Reverse().ToArray();
                    var strategyValues = new ConfiglueMergeSourceValue[strategySourceOrder.Length];
                    for (var index = 0; index < strategySourceOrder.Length; index++)
                    {
                        var source = strategySourceOrder[index];
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
                var sourceOrder = GetActiveSources().Reverse().ToArray();
                var targetIndex = Array.FindIndex(
                    sourceOrder,
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
                        sourceOrder,
                        targetIndex,
                        valuesBySource,
                        desired,
                        member.Name
                    ),
                    MergeMode.SetUnion => PlanSetUnionContribution(
                        sourceOrder,
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

        if (
            desired.Count < prefix.Count + suffix.Count
            || !desired.Take(prefix.Count).SequenceEqual(prefix)
            || !desired.Skip(desired.Count - suffix.Count).SequenceEqual(suffix)
        )
        {
            throw LogConflict(
                $"The edit to append-merged member '{memberName}' cannot be represented by source '{sourceOrder[targetIndex].Id}' while preserving other source contributions."
            );
        }

        return desired
            .Skip(prefix.Count)
            .Take(desired.Count - prefix.Count - suffix.Count)
            .ToList();
    }

    private List<object?> PlanSetUnionContribution(
        IReadOnlyList<StateSource<TFragment>> sourceOrder,
        int targetIndex,
        IReadOnlyDictionary<string, List<object?>> valuesBySource,
        IReadOnlyList<object?> desired,
        ConfiglueMemberSchema member
    )
    {
        var otherValues = new List<object?>();
        foreach (
            var values in sourceOrder
                .Where(
                    (source, index) => index != targetIndex && valuesBySource.ContainsKey(source.Id)
                )
                .Select(source => valuesBySource[source.Id])
        )
        {
            otherValues.AddRange(values.Where(value => !otherValues.Contains(value)));
        }

        if (otherValues.Any(value => !desired.Contains(value)))
        {
            throw LogConflict(
                $"The edit to set-union member '{member.Name}' removes a value contributed by another source."
            );
        }

        var targetValues = desired.Where(value => !otherValues.Contains(value)).ToList();
        var merged = new List<object?>();
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

            merged.AddRange(values.Where(value => !merged.Contains(value)));
        }

        var isSet =
            member.ValueType.IsGenericType
            && (
                member.ValueType.GetGenericTypeDefinition() == typeof(ISet<>)
                || member.ValueType.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)
                || member.ValueType.GetGenericTypeDefinition() == typeof(HashSet<>)
            );
        var matchesDesired = isSet
            ? merged.Count == desired.Distinct().Count() && merged.All(desired.Contains)
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
