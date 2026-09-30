using System.Collections;

namespace Configlue;

/// <summary>Compares sparse generated fragments while preserving member presence.</summary>
public static class ConfiglueFragmentComparer
{
    /// <summary>Compares two generated fragments, including nested fragments and collection contents.</summary>
    public static bool AreEqual<TFragment>(TFragment? left, TFragment? right)
        where TFragment : class, IConfiglueFragment<TFragment> =>
        AreEqual((IConfiglueFragment?)left, right);

    private static bool AreEqual(IConfiglueFragment? left, IConfiglueFragment? right)
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

        var leftMembers = left.EnumeratePresentMembers().ToDictionary(static member => member.Id);
        var rightMembers = right.EnumeratePresentMembers().ToDictionary(static member => member.Id);
        return leftMembers.Count == rightMembers.Count
            && leftMembers.All(pair =>
                rightMembers.TryGetValue(pair.Key, out var rightMember)
                && ValuesEqual(pair.Value.Value, rightMember.Value)
            );
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left is IConfiglueFragment leftFragment && right is IConfiglueFragment rightFragment)
        {
            return AreEqual(leftFragment, rightFragment);
        }

        if (left is IDictionary leftDictionary && right is IDictionary rightDictionary)
        {
            if (leftDictionary.Count != rightDictionary.Count)
            {
                return false;
            }

            foreach (DictionaryEntry entry in leftDictionary)
            {
                if (
                    !rightDictionary.Contains(entry.Key)
                    || !ValuesEqual(entry.Value, rightDictionary[entry.Key])
                )
                {
                    return false;
                }
            }

            return true;
        }

        if (
            left is IEnumerable leftItems
            && right is IEnumerable rightItems
            && left is not string
            && right is not string
        )
        {
            var leftValues = leftItems.Cast<object?>().ToArray();
            var rightValues = rightItems.Cast<object?>().ToArray();
            if (leftValues.Length != rightValues.Length)
            {
                return false;
            }

            if (IsSet(left.GetType()) && IsSet(right.GetType()))
            {
                var remaining = rightValues.ToList();
                foreach (var value in leftValues)
                {
                    var match = remaining.FindIndex(candidate => ValuesEqual(value, candidate));
                    if (match < 0)
                    {
                        return false;
                    }

                    remaining.RemoveAt(match);
                }

                return true;
            }

            for (var index = 0; index < leftValues.Length; index++)
            {
                if (!ValuesEqual(leftValues[index], rightValues[index]))
                {
                    return false;
                }
            }

            return true;
        }

        return Equals(left, right);
    }

    private static bool IsSet(Type type) =>
        type.GetInterfaces()
            .Any(static implemented =>
                implemented.IsGenericType
                && (
                    implemented.GetGenericTypeDefinition() == typeof(ISet<>)
                    || implemented.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)
                )
            );
}
