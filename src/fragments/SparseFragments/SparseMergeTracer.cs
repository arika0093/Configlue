using System.Collections;
using System.ComponentModel;

namespace SparseFragments;

/// <summary>Explains which contributions determined one effective fragment member.</summary>
/// <remarks>Advanced diagnostics vocabulary: returned by <see cref="SparseMergeTracer"/>.</remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class SparseMergeMemberProvenance
{
    /// <summary>Initializes member provenance.</summary>
    /// <param name="memberId">The schema-local member ordinal.</param>
    /// <param name="name">The member name.</param>
    /// <param name="value">The effective presence-aware member value.</param>
    /// <param name="contributionIndices">The contribution indices that determined the member.</param>
    /// <param name="elements">Per-element provenance for collection members.</param>
    /// <param name="nested">Nested member provenance for deeply merged structural members.</param>
    public SparseMergeMemberProvenance(
        int memberId,
        string name,
        Optional<object?> value,
        IEnumerable<int> contributionIndices,
        IEnumerable<SparseMergeElementProvenance>? elements = null,
        IEnumerable<SparseMergeMemberProvenance>? nested = null
    )
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(contributionIndices);
        MemberId = memberId;
        Name = name;
        Value = value;
        ContributionIndices = Array.AsReadOnly(contributionIndices.ToArray());
        Elements = Array.AsReadOnly((elements ?? []).ToArray());
        Nested = Array.AsReadOnly((nested ?? []).ToArray());
    }

    /// <summary>The schema-local member ordinal.</summary>
    public int MemberId { get; }

    /// <summary>The member name.</summary>
    public string Name { get; }

    /// <summary>The effective presence-aware member value.</summary>
    public Optional<object?> Value { get; }

    /// <summary>The contribution indices that determined the member, low to high priority.</summary>
    public IReadOnlyList<int> ContributionIndices { get; }

    /// <summary>Per-element provenance when the member merged as a collection.</summary>
    public IReadOnlyList<SparseMergeElementProvenance> Elements { get; }

    /// <summary>Nested member provenance when the member merged deeply.</summary>
    public IReadOnlyList<SparseMergeMemberProvenance> Nested { get; }
}

/// <summary>Computes contribution provenance for a merged sparse fragment without any host metadata.</summary>
/// <remarks>
/// Advanced opt-in diagnostics: request provenance explicitly where the merge mode gives it clear meaning.
/// Normal fragment reads never retain contribution data for this API.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public static class SparseMergeTracer
{
    /// <summary>Explains the effective members of one fragment level.</summary>
    /// <param name="schema">The schema describing the fragment shape.</param>
    /// <param name="effective">The effective merged fragment, or null when the root is present-null.</param>
    /// <param name="contributionsLowToHigh">The contributions ordered from lowest to highest priority.</param>
    public static IReadOnlyList<SparseMergeMemberProvenance> Explain(
        SparseFragmentSchema schema,
        ISparseFragment? effective,
        IReadOnlyList<SparseContribution> contributionsLowToHigh
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(contributionsLowToHigh);

        var rootReset = -1;
        for (var index = 0; index < contributionsLowToHigh.Count; index++)
            if (
                contributionsLowToHigh[index].Value.IsPresent
                && contributionsLowToHigh[index].Value.Value is null
            )
                rootReset = index;
        if (rootReset >= 0)
            contributionsLowToHigh = contributionsLowToHigh.Skip(rootReset + 1).ToArray();

        var results = new List<SparseMergeMemberProvenance>(schema.Members.Count);
        foreach (var member in schema.Members)
        {
            var effectiveValue = ReadMember(effective, member.Id);
            var contributionValues = contributionsLowToHigh
                .Select(contribution => ReadMember(contribution.Value, member.Id))
                .ToArray();

            if (
                member.CollectionMergeStrategy is null
                && member.MergeMode is MergeMode.Deep or MergeMode.Append or MergeMode.SetUnion
            )
            {
                var lastReset = -1;
                for (var index = 0; index < contributionValues.Length; index++)
                    if (
                        contributionValues[index].IsPresent
                        && contributionValues[index].Value is null
                    )
                        lastReset = index;
                for (var index = 0; index < lastReset; index++)
                    contributionValues[index] = Optional<object?>.Missing;
            }

            var presentIndices = new List<int>();
            for (var index = 0; index < contributionsLowToHigh.Count; index++)
            {
                if (contributionValues[index].IsPresent)
                {
                    presentIndices.Add(contributionsLowToHigh[index].Index);
                }
            }

            var elements = ExplainElements(
                member,
                effectiveValue,
                contributionValues,
                contributionsLowToHigh
            );
            var contributionIndices = ExplainMemberIndices(member, presentIndices);
            IReadOnlyList<SparseMergeMemberProvenance> nested = [];
            if (
                member.MergeMode == MergeMode.Deep
                && member.NestedSchemaFactory is not null
                && effectiveValue.IsPresent
                && effectiveValue.Value is ISparseFragment nestedEffective
            )
            {
                var nestedContributions = new List<SparseContribution>();
                for (var index = 0; index < contributionsLowToHigh.Count; index++)
                {
                    nestedContributions.Add(
                        new SparseContribution(
                            contributionsLowToHigh[index].Index,
                            contributionValues[index]
                        )
                    );
                }

                nested = Explain(
                    member.NestedSchemaFactory(),
                    nestedEffective,
                    nestedContributions
                );
            }

            results.Add(
                new SparseMergeMemberProvenance(
                    member.Id,
                    member.Name,
                    effectiveValue,
                    contributionIndices,
                    elements,
                    nested
                )
            );
        }

        return Array.AsReadOnly(results.ToArray());
    }

    private static IReadOnlyList<int> ExplainMemberIndices(
        SparseFragmentMemberSchema member,
        List<int> presentIndices
    )
    {
        if (presentIndices.Count == 0)
        {
            return [];
        }

        if (member.MergeMode == MergeMode.Append || member.MergeMode == MergeMode.SetUnion)
        {
            return presentIndices;
        }

        if (member.CollectionMergeStrategy is not null)
        {
            return presentIndices;
        }

        if (member.MergeMode == MergeMode.Deep)
        {
            return presentIndices;
        }

        return [presentIndices[presentIndices.Count - 1]];
    }

    private static IReadOnlyList<SparseMergeElementProvenance> ExplainElements(
        SparseFragmentMemberSchema member,
        Optional<object?> effectiveValue,
        Optional<object?>[] contributionValues,
        IReadOnlyList<SparseContribution> contributionsLowToHigh
    )
    {
        if (!effectiveValue.IsPresent || effectiveValue.Value is null)
        {
            return [];
        }

        var effectiveElements = ToList(effectiveValue.Value);
        if (effectiveElements.Count == 0)
        {
            return [];
        }

        if (member.CollectionMergeStrategy is not null)
        {
            var typedContributions = new SparseContribution[contributionsLowToHigh.Count];
            for (var index = 0; index < contributionsLowToHigh.Count; index++)
            {
                typedContributions[index] = new SparseContribution(
                    contributionsLowToHigh[index].Index,
                    contributionValues[index]
                );
            }

            if (member.CollectionMergeStrategy is ISparseMergeElementProvenanceProvider provider)
            {
                return provider.ExplainElementsObject(effectiveValue.Value, typedContributions);
            }

            return [];
        }

        var contributions = contributionsLowToHigh
            .Select((contribution, index) => (contribution.Index, Value: contributionValues[index]))
            .Where(static contribution => contribution.Value.IsPresent)
            .Select(static contribution => (contribution.Index, contribution.Value.Value))
            .ToArray();
        return CollectionProvenance.Explain(
            member.MergeMode,
            effectiveValue.Value,
            contributions,
            member.ContainsElement
        );
    }

    private static Optional<object?> ReadMember(ISparseFragment? fragment, int memberId)
    {
        if (fragment is null)
        {
            return Optional<object?>.Missing;
        }

        foreach (var member in fragment.EnumeratePresentMembers())
        {
            if (member.Id == memberId)
            {
                return Optional<object?>.Present(member.Value);
            }
        }

        return Optional<object?>.Missing;
    }

    private static Optional<object?> ReadMember(Optional<object?> contribution, int memberId) =>
        contribution.IsPresent
            ? ReadMember(contribution.Value as ISparseFragment, memberId)
            : Optional<object?>.Missing;

    private static List<object?> ToList(object? value) =>
        value is IEnumerable elements && value is not string
            ? elements.Cast<object?>().ToList()
            : [];
}
