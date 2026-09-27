using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace Configlue;

public sealed partial class ConfiglueOptions<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private TModel CloneModel(TModel value)
    {
        var clone = _cloneStrategy is null ? value.DeepClone() : _cloneStrategy(value);
        if (clone is null || ReferenceEquals(clone, value))
        {
            throw new InvalidOperationException(
                "The model clone strategy must return a non-null, distinct model instance."
            );
        }

        return clone;
    }

    private static TFragment CloneFragment(TFragment value) =>
        value is IConfiglueDeepCloneable<TFragment> cloneable ? cloneable.DeepClone() : value;

    TModel IConfiglueValueCloneProvider<TModel>.CloneValue(TModel value) => CloneModel(value);

    private static object? GetModelValue(
        ConfiglueModelSchema schema,
        object model,
        IReadOnlyList<string> path,
        string propertyPath,
        out ConfiglueMemberSchema leafMember
    )
    {
        leafMember = default;
        object? current = model;
        for (var index = 0; index < path.Count; index++)
        {
            var member = schema.Members.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, path[index], StringComparison.Ordinal)
            );
            if (string.IsNullOrEmpty(member.Name))
            {
                throw new ArgumentException(
                    $"Model '{schema.ModelType}' has no member named '{path[index]}' in property path '{propertyPath}'.",
                    nameof(propertyPath)
                );
            }

            object? value = null;
            if (current is not null)
            {
                var getter =
                    member.GetValue
                    ?? throw new InvalidOperationException(
                        $"Generated getter metadata is missing for '{schema.ModelType}.{member.Name}'."
                    );
                value = getter(current);
            }

            if (index == path.Count - 1)
            {
                leafMember = member;
                return value;
            }

            if (member.NestedSchemaFactory is null)
            {
                throw new ArgumentException(
                    $"Member '{schema.ModelType}.{member.Name}' is not a generated nested model.",
                    nameof(propertyPath)
                );
            }

            schema = member.NestedSchemaFactory();
            current = value;
        }

        throw new ArgumentException("The property path is empty.", nameof(propertyPath));
    }

    private sealed record CollectionElementProvenance(
        int Index,
        object? Value,
        int[] SourceIndices
    );

    private static IReadOnlyList<CollectionElementProvenance> CollectCollectionElementProvenance(
        ConfiglueMemberSchema member,
        object? effectiveValue,
        IReadOnlyList<(string SourceId, object? Value)> sourceContributions
    )
    {
        var effectiveElements = GetCollectionElements(effectiveValue);
        var elementSources = Enumerable
            .Range(0, effectiveElements.Length)
            .Select(static _ => new List<int>())
            .ToArray();

        if (member.MergeStrategy is { } mergeStrategy)
        {
            var sourcePriority = sourceContributions
                .Select((source, index) => (source.SourceId, index))
                .ToDictionary(
                    static source => source.SourceId,
                    static source => source.index,
                    StringComparer.Ordinal
                );
            var sourceValues = sourceContributions
                .Reverse()
                .Select(static source => new ConfiglueMergeSourceValue(
                    source.SourceId,
                    Optional<object?>.Present(source.Value)
                ))
                .ToArray();
            foreach (var provenance in mergeStrategy.ExplainElements(effectiveValue, sourceValues))
            {
                if (provenance.Index >= effectiveElements.Length)
                {
                    throw new InvalidOperationException(
                        $"Merge strategy for '{member.Name}' returned provenance for out-of-range element index {provenance.Index}."
                    );
                }

                var orderedSourceIndices = new SortedSet<int>();
                foreach (var sourceId in provenance.SourceIds)
                {
                    if (!sourcePriority.TryGetValue(sourceId, out var sourceIndex))
                    {
                        throw new InvalidOperationException(
                            $"Merge strategy for '{member.Name}' referenced unknown source '{sourceId}' in element provenance."
                        );
                    }

                    orderedSourceIndices.Add(sourceIndex);
                }

                elementSources[provenance.Index].AddRange(orderedSourceIndices);
            }

            return BuildProvenance(effectiveElements, elementSources);
        }

        if (member.MergeMode == MergeMode.Append && effectiveValue is IList)
        {
            var expectedElementCount = sourceContributions.Sum(contribution =>
                GetCollectionElements(contribution.Value).Length
            );
            if (expectedElementCount == effectiveElements.Length)
            {
                var elementIndex = 0;
                foreach (
                    var (contribution, sourceIndex) in sourceContributions
                        .Select((candidate, index) => (candidate, index))
                        .Reverse()
                )
                {
                    foreach (var _ in GetCollectionElements(contribution.Value))
                    {
                        elementSources[elementIndex].Add(sourceIndex);
                        elementIndex++;
                    }
                }
            }
            else
            {
                AddMatchingElementSources(effectiveElements, sourceContributions, elementSources);
            }
        }
        else
        {
            var eligibleSources =
                member.MergeMode == MergeMode.Replace
                    ? sourceContributions.Take(1)
                    : sourceContributions;
            AddMatchingElementSources(effectiveElements, eligibleSources, elementSources);
        }

        return BuildProvenance(effectiveElements, elementSources);
    }

    private static IReadOnlyList<CollectionElementProvenance> BuildProvenance(
        object?[] effectiveElements,
        IReadOnlyList<List<int>> elementSources
    ) =>
        Array.AsReadOnly(
            effectiveElements
                .Select(
                    (value, index) =>
                        new CollectionElementProvenance(
                            index,
                            value,
                            elementSources[index].ToArray()
                        )
                )
                .ToArray()
        );

    private static object?[] GetCollectionElements(object? value) =>
        value is IEnumerable elements && value is not string
            ? elements.Cast<object?>().ToArray()
            : [];

    private static void AddMatchingElementSources(
        IReadOnlyList<object?> effectiveElements,
        IEnumerable<(string SourceId, object? Value)> sourceContributions,
        IReadOnlyList<List<int>> elementSources
    )
    {
        var indexed = sourceContributions
            .Select((source, index) => (source.SourceId, source.Value, index))
            .ToArray();
        foreach (
            var (element, elementIndex) in effectiveElements.Select(
                (value, index) => (value, index)
            )
        )
        {
            foreach (
                var source in indexed.Where(source =>
                    GetCollectionElements(source.Value)
                        .Any(sourceElement => Equals(sourceElement, element))
                )
            )
            {
                elementSources[elementIndex].Add(source.index);
            }
        }
    }

    private static bool TryGetFragmentValue(
        IConfiglueFragment fragment,
        IReadOnlyList<string> path,
        out object? value
    )
    {
        IConfiglueFragment current = fragment;
        for (var index = 0; index < path.Count; index++)
        {
            var found = false;
            object? currentValue = null;
            foreach (var member in current.EnumeratePresentMembers())
            {
                if (!string.Equals(member.Name, path[index], StringComparison.Ordinal))
                {
                    continue;
                }

                currentValue = member.Value;
                found = true;
                break;
            }

            if (!found)
            {
                value = null;
                return false;
            }

            if (index == path.Count - 1)
            {
                value = currentValue;
                return true;
            }

            if (currentValue is not IConfiglueFragment nested)
            {
                value = null;
                return false;
            }

            current = nested;
        }

        value = null;
        return false;
    }

    private static bool HaveSameRevisions(StateRevisionVector? left, StateRevisionVector? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Revisions.Count != right.Revisions.Count)
        {
            return false;
        }

        return left.Revisions.All(pair =>
                right.TryGetRevision(pair.Key, out var revision)
                && string.Equals(pair.Value, revision, StringComparison.Ordinal)
            )
            && left.NestedRevisions.Count == right.NestedRevisions.Count
            && left.NestedRevisions.All(pair =>
                right.TryGetNestedRevisions(pair.Key, out var nested)
                && HaveSameRevisions(pair.Value, nested)
            );
    }

    private static TaskCompletionSource NewTopologySignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RemoveChangeListener(Action<TModel> listener)
    {
        lock (_changeGate)
        {
            _changeListeners.Remove(listener);
        }
    }

    private void RemoveReloadFailureListener(Action<Exception> listener)
    {
        lock (_changeGate)
        {
            _reloadFailureListeners.Remove(listener);
        }
    }

    private sealed class FragmentChangesPatch(TFragment changes) : IConfiglueMemberPatch
    {
        public TFragment Changes => changes;

        public ConfiglueModelSchema Schema => TModel.ConfiglueSchema;

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
            var selected = TFragment.Empty;
            foreach (var member in changes.EnumeratePresentMembers())
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

            return new FragmentChangesPatch(selected);
        }
    }

    private sealed record ResolvedContribution(
        StateSource<TFragment> Source,
        StateReadResult<TFragment> Result,
        bool IsModelDefaults = false
    );

    private sealed class ModelDefaultsReader(TFragment fragment) : IStateReader<TFragment>
    {
        public ValueTask<StateReadResult<TFragment>> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(StateReadResult<TFragment>.Success(fragment));
        }
    }

    private sealed record ResolvedFailure(
        StateSource<TFragment> Source,
        StateReadResult<TFragment> Result
    );

    private sealed record ResolvedState(
        StateReadResult<TModel> Result,
        IReadOnlyList<ResolvedContribution> Contributions,
        TFragment? MergedFragment,
        IReadOnlyList<ResolvedFailure> Failures
    );

    private sealed class ChangeSubscription(
        ConfiglueOptions<TModel, TFragment> owner,
        Action<TModel> listener
    ) : IDisposable
    {
        private ConfiglueOptions<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveChangeListener(listener);
    }

    private sealed class ReloadFailureSubscription(
        ConfiglueOptions<TModel, TFragment> owner,
        Action<Exception> listener
    ) : IDisposable
    {
        private ConfiglueOptions<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveReloadFailureListener(listener);
    }

    private static bool CanFallBack(StateFallbackCondition condition, StateReadStatus status) =>
        status switch
        {
            StateReadStatus.NotFound => (condition & StateFallbackCondition.NotFound) != 0,
            StateReadStatus.Unavailable => (condition & StateFallbackCondition.Unavailable) != 0,
            StateReadStatus.Invalid => (condition & StateFallbackCondition.Invalid) != 0,
            _ => false,
        };
}
