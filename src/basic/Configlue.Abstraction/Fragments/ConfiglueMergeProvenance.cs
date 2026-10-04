using System.Collections;

namespace Configlue;

/// <summary>Identifies the source contributions that supplied one effective collection element.</summary>
/// <remarks>Advanced merge SPI result.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class ConfiglueCollectionElementProvenance
{
    /// <summary>Initializes element provenance.</summary>
    /// <param name="index">The effective collection index.</param>
    /// <param name="value">The effective element value.</param>
    /// <param name="sourceIndices">The low-to-high contribution indices that supplied the element.</param>
    public ConfiglueCollectionElementProvenance(
        int index,
        object? value,
        IEnumerable<int> sourceIndices
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(sourceIndices);
        Index = index;
        Value = value;
        SourceIndices = Array.AsReadOnly(sourceIndices.ToArray());
    }

    /// <summary>The effective collection index.</summary>
    public int Index { get; }

    /// <summary>The effective element value.</summary>
    public object? Value { get; }

    /// <summary>The low-to-high contribution indices that supplied the element.</summary>
    public IReadOnlyList<int> SourceIndices { get; }
}

/// <summary>
/// Product-neutral per-element provenance for a merged collection member. Host source identities are supplied only to
/// resolve custom strategy provenance back to their contribution positions.
/// </summary>
/// <remarks>Runtime helper consumed by details; custom strategies expose provenance through
/// <see cref="IConfiglueMergeElementProvenanceProvider"/> instead of calling this type.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class ConfiglueMergeProvenance
{
    /// <summary>Maps effective collection elements to their contributing source positions.</summary>
    /// <param name="member">The member schema.</param>
    /// <param name="effectiveValue">The effective collection value.</param>
    /// <param name="sourceContributions">The contributions ordered from highest to lowest priority.</param>
    public static IReadOnlyList<ConfiglueCollectionElementProvenance> ExplainElements(
        ConfiglueMemberSchema member,
        object? effectiveValue,
        IReadOnlyList<(SourceId SourceId, object? Value)> sourceContributions
    )
    {
        ArgumentNullException.ThrowIfNull(sourceContributions);
        var effectiveElements = GetCollectionElements(effectiveValue);
        var elementSources = Enumerable
            .Range(0, effectiveElements.Length)
            .Select(static _ => new List<int>())
            .ToArray();

        var mergeStrategy = member.MergeStrategy;
        if (mergeStrategy is not null)
        {
            if (mergeStrategy is not IConfiglueMergeElementProvenanceProvider provenanceProvider)
            {
                return effectiveElements
                    .Select(
                        (element, index) =>
                            new ConfiglueCollectionElementProvenance(index, element, [])
                    )
                    .ToArray();
            }

            var sourcePriority = sourceContributions
                .Select((source, index) => (source.SourceId, index))
                .ToDictionary(static source => source.SourceId, static source => source.index);
            var sourceValues = sourceContributions
                .Reverse()
                .Select(static source => new ConfiglueMergeSourceValue(
                    source.SourceId,
                    Optional<object?>.Present(source.Value)
                ))
                .ToArray();
            foreach (
                var provenance in provenanceProvider.ExplainElementsObject(
                    effectiveValue,
                    sourceValues
                )
            )
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

        // Runtime contributions arrive highest priority first; the neutral algebra consumes lowest first.
        var contributions = sourceContributions
            .Select((contribution, index) => (Index: index, contribution.Value))
            .Reverse()
            .ToArray();
        return CollectionProvenance
            .Explain(member.MergeMode, effectiveValue, contributions, member.ContainsElement)
            .Select(element => new ConfiglueCollectionElementProvenance(
                element.Index,
                element.Value,
                element.SourceIndices.OrderBy(static index => index)
            ))
            .ToArray();
    }

    private static IReadOnlyList<ConfiglueCollectionElementProvenance> BuildProvenance(
        object?[] effectiveElements,
        IReadOnlyList<List<int>> elementSources
    ) =>
        Array.AsReadOnly(
            effectiveElements
                .Select(
                    (value, index) =>
                        new ConfiglueCollectionElementProvenance(
                            index,
                            value,
                            elementSources[index]
                        )
                )
                .ToArray()
        );

    private static object?[] GetCollectionElements(object? value) =>
        value is IEnumerable elements && value is not string
            ? elements.Cast<object?>().ToArray()
            : [];
}
