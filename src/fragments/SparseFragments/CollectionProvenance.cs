using System.Collections;
#if CONFIGLUE_FRAGMENT_RUNTIME
using ElementProvenance = Configlue.ConfiglueCollectionElementProvenance;
using ValueComparer = Configlue.ConfiglueValueComparer;

namespace Configlue;

#else
using ElementProvenance = SparseFragments.SparseMergeElementProvenance;
using ValueComparer = SparseFragments.SparseValueComparer;

namespace SparseFragments;

#endif

/// <summary>Collection provenance rules shared by standalone traces and host metadata adapters.</summary>
internal static class CollectionProvenance
{
    public static IReadOnlyList<ElementProvenance> Explain(
        MergeMode mode,
        object? effective,
        IReadOnlyList<(int Index, object? Value)> contributionsLowToHigh,
        Func<object, object?, bool>? containsElement = null
    )
    {
        if (mode is MergeMode.Append or MergeMode.SetUnion)
        {
            var reset = -1;
            for (var index = 0; index < contributionsLowToHigh.Count; index++)
                if (contributionsLowToHigh[index].Value is null)
                    reset = index;
            if (reset >= 0)
                contributionsLowToHigh = contributionsLowToHigh.Skip(reset).ToArray();
        }
        var elements = Elements(effective);
        var sources = elements.Select(static _ => new List<int>()).ToArray();
        if (
            mode == MergeMode.Append
            && contributionsLowToHigh.Sum(contribution => Elements(contribution.Value).Length)
                == elements.Length
        )
        {
            var elementIndex = 0;
            foreach (var contribution in contributionsLowToHigh)
            {
                foreach (var _ in Elements(contribution.Value))
                {
                    sources[elementIndex++].Add(contribution.Index);
                }
            }
        }
        else
        {
            var eligible =
                mode == MergeMode.Replace
                    ? contributionsLowToHigh.Skip(Math.Max(0, contributionsLowToHigh.Count - 1))
                    : contributionsLowToHigh;
            foreach (var contribution in eligible)
            {
                var candidates = Elements(contribution.Value);
                for (var index = 0; index < elements.Length; index++)
                    if (
                        contribution.Value is not null && containsElement is not null
                            ? containsElement(contribution.Value, elements[index])
                            : candidates.Any(candidate =>
                                ValueComparer.AreEqual(candidate, elements[index])
                            )
                    )
                        sources[index].Add(contribution.Index);
            }
        }

        return Array.AsReadOnly(
            Enumerable
                .Range(0, elements.Length)
                .Select(index =>
#if CONFIGLUE_FRAGMENT_RUNTIME
                new ElementProvenance(index, elements[index], sources[index])
#else
                new ElementProvenance(index, sources[index])
#endif
                )
                .ToArray()
        );
    }

    private static object?[] Elements(object? value) =>
        value is IEnumerable elements && value is not string
            ? elements.Cast<object?>().ToArray()
            : [];
}
