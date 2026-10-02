namespace SparseFragments;

/// <summary>Structural equality for sparse fragments that preserves presence and nesting without a host model.</summary>
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

        var leftMembers = ToMap(left);
        var rightMembers = ToMap(right);
        if (leftMembers.Count != rightMembers.Count)
        {
            return false;
        }

        foreach (var pair in leftMembers)
        {
            if (!rightMembers.TryGetValue(pair.Key, out var other))
            {
                return false;
            }

            if (pair.Value is ISparseFragment leftFragment)
            {
                if (
                    other is not ISparseFragment rightFragment
                    || !AreEqual(leftFragment, rightFragment)
                )
                {
                    return false;
                }

                continue;
            }

            if (!SparseValueComparer.AreEqual(pair.Value, other))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<int, object?> ToMap(ISparseFragment fragment)
    {
        var map = new Dictionary<int, object?>();
        foreach (var member in fragment.EnumeratePresentMembers())
        {
            map[member.Id] = member.Value;
        }

        return map;
    }
}
