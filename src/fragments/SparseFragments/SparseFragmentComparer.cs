using System.ComponentModel;

namespace SparseFragments;

/// <summary>Structural equality for sparse fragments that preserves presence and nesting without a host model.</summary>
/// <remarks>
/// Intentional advanced API: the same semantics back generated diff and patch rebase.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public static class SparseFragmentComparer
{
    /// <summary>Compares two presence-aware fragment states.</summary>
    /// <typeparam name="TFragment">The generated fragment type.</typeparam>
    public static bool AreEqual<TFragment>(Optional<TFragment?> left, Optional<TFragment?> right)
        where TFragment : class, ISparseFragment =>
        left.IsPresent == right.IsPresent
        && AreEqual(left.IsPresent ? left.Value : null, right.IsPresent ? right.Value : null);

    /// <summary>Compares two fragment instances member by member.</summary>
    public static bool AreEqual(ISparseFragment? left, ISparseFragment? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left.GetType() != right.GetType())
        {
            return false;
        }

        // Generated fragments enumerate present members in declaration order, so members
        // can be compared pairwise without materializing lookup dictionaries.
        using var leftMembers = left.EnumeratePresentMembers().GetEnumerator();
        using var rightMembers = right.EnumeratePresentMembers().GetEnumerator();
        while (true)
        {
            var leftMoved = leftMembers.MoveNext();
            var rightMoved = rightMembers.MoveNext();
            if (leftMoved != rightMoved)
            {
                return false;
            }

            if (!leftMoved)
            {
                return true;
            }

            if (leftMembers.Current.Id != rightMembers.Current.Id)
            {
                return false;
            }

            if (
                !FragmentComparisonPrimitives.AreValuesEqual(
                    leftMembers.Current.Value,
                    rightMembers.Current.Value
                )
            )
            {
                return false;
            }
        }
    }
}
