using Configlue.CompilerServices;

namespace Configlue;

/// <summary>Compares sparse generated fragments while preserving member presence.</summary>
internal static class ConfiglueFragmentComparer
{
    /// <summary>Compares two generated fragments, including nested fragments and collection contents.</summary>
    internal static bool AreEqual<TFragment>(TFragment? left, TFragment? right)
        where TFragment : class, IConfiglueFragment<TFragment> =>
        AreEqual((IConfiglueFragment?)left, right);

    internal static bool AreEqual(IConfiglueFragment? left, IConfiglueFragment? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (
            left is null
            || right is null
            || left.Schema.ModelType != right.Schema.ModelType
            || left.Schema.Id != right.Schema.Id
            || left.Schema.Version != right.Schema.Version
        )
        {
            return false;
        }

        if (
            left is IConfiglueOrdinalDynamicFragment leftOrdinal
            && right is IConfiglueOrdinalDynamicFragment rightOrdinal
        )
        {
            var count = leftOrdinal.PresentMemberCount;
            if (count != rightOrdinal.PresentMemberCount)
            {
                return false;
            }

            for (var index = 0; index < count; index++)
            {
                var leftMember = leftOrdinal.GetPresentMember(index);
                var rightMember = rightOrdinal.GetPresentMember(index);
                if (leftMember.Id != rightMember.Id)
                {
                    // Generated fragments enumerate present members in declaration order,
                    // so same-type fragments disagreeing at one position are unequal.
                    // Fall back to order-independent matching only for mixed runtimes.
                    if (left.GetType() == right.GetType())
                    {
                        return false;
                    }

                    return MembersEqualSlow(left, right);
                }

                if (
                    !FragmentComparisonPrimitives.AreValuesEqual(
                        leftMember.Value,
                        rightMember.Value
                    )
                )
                {
                    return false;
                }
            }

            return true;
        }

        return MembersEqualSlow(left, right);
    }

    private static bool MembersEqualSlow(IConfiglueFragment left, IConfiglueFragment right)
    {
        var leftMembers = new Dictionary<int, object?>();
        foreach (var member in left.EnumeratePresentMembersFast())
        {
            leftMembers[member.Id] = member.Value;
        }

        foreach (var member in right.EnumeratePresentMembersFast())
        {
            if (!leftMembers.TryGetValue(member.Id, out var leftValue))
            {
                return false;
            }

            leftMembers.Remove(member.Id);
            if (!FragmentComparisonPrimitives.AreValuesEqual(leftValue, member.Value))
            {
                return false;
            }
        }

        return leftMembers.Count == 0;
    }
}
