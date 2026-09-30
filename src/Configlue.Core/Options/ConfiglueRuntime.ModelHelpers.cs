using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Configlue.CompilerServices;
using Configlue.Resources;
using Configlue.Sources;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
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

    private sealed record CollectionElementProvenance
    {
        public int Index { get; init; }
        public object? Value { get; init; }
        public int[] SourceIndices { get; init; }

        public CollectionElementProvenance(int Index, object? Value, int[] SourceIndices)
        {
            this.Index = Index;
            this.Value = Value;
            this.SourceIndices = SourceIndices;
        }

        public void Deconstruct(out int Index, out object? Value, out int[] SourceIndices)
        {
            Index = this.Index;
            Value = this.Value;
            SourceIndices = this.SourceIndices;
        }
    }

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

        var mergeStrategy = member.MergeStrategy;
        if (mergeStrategy is not null)
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

        public ConfiglueModelSchema Schema => ModelSchema;

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
            var selected = EmptyFragment;
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

    private readonly record struct ResolvedContribution
    {
        public StateSource<TFragment> Source { get; init; }
        public StateReadResult<TFragment> Result { get; init; }
        public bool IsModelDefaults { get; init; }
        public ConfiglueResourceContext? ResourceContext { get; init; }
        public ResourceId? ResourceId { get; init; }

        public ResolvedContribution(
            StateSource<TFragment> Source,
            StateReadResult<TFragment> Result,
            bool IsModelDefaults = false,
            ConfiglueResourceContext? ResourceContext = null,
            ResourceId? ResourceId = null
        )
        {
            this.Source = Source;
            this.Result = Result;
            this.IsModelDefaults = IsModelDefaults;
            this.ResourceContext = ResourceContext;
            this.ResourceId = ResourceId;
        }

        public void Deconstruct(
            out StateSource<TFragment> Source,
            out StateReadResult<TFragment> Result,
            out bool IsModelDefaults
        )
        {
            Source = this.Source;
            Result = this.Result;
            IsModelDefaults = this.IsModelDefaults;
        }
    }

    private sealed class ModelDefaultsReader(TFragment fragment) : ISourceReader<TFragment>
    {
        public ValueTask<StateReadResult<TFragment>> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<StateReadResult<TFragment>>(
                StateReadResult<TFragment>.Success(fragment)
            );
        }
    }

    private readonly record struct ResolvedFailure
    {
        public StateSource<TFragment> Source { get; init; }
        public StateReadResult<TFragment> Result { get; init; }
        public ConfiglueResourceContext? ResourceContext { get; init; }
        public ResourceId? ResourceId { get; init; }

        public ResolvedFailure(
            StateSource<TFragment> Source,
            StateReadResult<TFragment> Result,
            ConfiglueResourceContext? ResourceContext = null,
            ResourceId? ResourceId = null
        )
        {
            this.Source = Source;
            this.Result = Result;
            this.ResourceContext = ResourceContext;
            this.ResourceId = ResourceId;
        }

        public void Deconstruct(
            out StateSource<TFragment> Source,
            out StateReadResult<TFragment> Result
        )
        {
            Source = this.Source;
            Result = this.Result;
        }
    }

    private readonly record struct ResolvedState
    {
        public StateReadResult<TModel> Result { get; init; }
        public IReadOnlyList<ResolvedContribution> Contributions { get; init; }
        public TFragment? MergedFragment { get; init; }
        public IReadOnlyList<ResolvedFailure> Failures { get; init; }

        public ResolvedState(
            StateReadResult<TModel> Result,
            IReadOnlyList<ResolvedContribution> Contributions,
            TFragment? MergedFragment,
            IReadOnlyList<ResolvedFailure> Failures
        )
        {
            this.Result = Result;
            this.Contributions = Contributions;
            this.MergedFragment = MergedFragment;
            this.Failures = Failures;
        }

        public void Deconstruct(
            out StateReadResult<TModel> Result,
            out IReadOnlyList<ResolvedContribution> Contributions,
            out TFragment? MergedFragment,
            out IReadOnlyList<ResolvedFailure> Failures
        )
        {
            Result = this.Result;
            Contributions = this.Contributions;
            MergedFragment = this.MergedFragment;
            Failures = this.Failures;
        }
    }

    private sealed class ChangeSubscription(
        ConfiglueRuntime<TModel, TFragment> owner,
        Action<TModel> listener
    ) : IDisposable
    {
        private ConfiglueRuntime<TModel, TFragment>? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.RemoveChangeListener(listener);
    }

    private sealed class ReloadFailureSubscription(
        ConfiglueRuntime<TModel, TFragment> owner,
        Action<Exception> listener
    ) : IDisposable
    {
        private ConfiglueRuntime<TModel, TFragment>? _owner = owner;

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
