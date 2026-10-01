using System.Collections;

namespace SparseFragments;

/// <summary>Default semantic equality used by generated sparse fragments.</summary>
public static class SparseValueComparer
{
    /// <summary>Compares two values, treating ordinary sequences element-wise.</summary>
    public static bool AreEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left is string || right is string)
        {
            return Equals(left, right);
        }

        if (left is IEnumerable leftItems && right is IEnumerable rightItems)
        {
            var leftEnumerator = leftItems.GetEnumerator();
            var rightEnumerator = rightItems.GetEnumerator();
            while (true)
            {
                var leftMoved = leftEnumerator.MoveNext();
                var rightMoved = rightEnumerator.MoveNext();
                if (leftMoved != rightMoved)
                {
                    return false;
                }

                if (!leftMoved)
                {
                    return true;
                }

                if (!AreEqual(leftEnumerator.Current, rightEnumerator.Current))
                {
                    return false;
                }
            }
        }

        return Equals(left, right);
    }

    /// <summary>Compares two typed values using the default sparse semantics.</summary>
    public static bool AreEqual<T>(T? left, T? right)
    {
        return AreEqual((object?)left, (object?)right);
    }

    /// <summary>Compares set-shaped values without depending on enumeration order.</summary>
    public static bool AreSetEqual<T>(IEnumerable<T>? left, IEnumerable<T>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left is ISet<T> leftSet)
        {
            return leftSet.SetEquals(right);
        }

        if (right is ISet<T> rightSet)
        {
            return rightSet.SetEquals(left);
        }

        return new HashSet<T>(left).SetEquals(right);
    }

    /// <summary>Compares dictionary-shaped values by key/value semantics.</summary>
    public static bool AreDictionaryEqual<TKey, TValue>(
        IEnumerable<KeyValuePair<TKey, TValue>>? left,
        IEnumerable<KeyValuePair<TKey, TValue>>? right
    )
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        var rightEntries = right.ToArray();
        if (left is IReadOnlyDictionary<TKey, TValue> readOnlyDictionary)
        {
            return DictionaryEquals(readOnlyDictionary, rightEntries);
        }

        if (left is IDictionary<TKey, TValue> dictionary)
        {
            return DictionaryEquals(dictionary, rightEntries);
        }

        var leftEntries = left.ToArray();
        return PairSequenceEquals(leftEntries, rightEntries);
    }

    private static bool DictionaryEquals<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> left,
        KeyValuePair<TKey, TValue>[] right
    )
    {
        if (left.Count != right.Length)
        {
            return false;
        }

        foreach (var pair in right)
        {
            if (!left.TryGetValue(pair.Key, out var value))
            {
                return false;
            }

            if (!AreEqual(value, pair.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DictionaryEquals<TKey, TValue>(
        IDictionary<TKey, TValue> left,
        KeyValuePair<TKey, TValue>[] right
    )
    {
        if (left.Count != right.Length)
        {
            return false;
        }

        foreach (var pair in right)
        {
            if (!left.TryGetValue(pair.Key, out var value))
            {
                return false;
            }

            if (!AreEqual(value, pair.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PairSequenceEquals<TKey, TValue>(
        KeyValuePair<TKey, TValue>[] left,
        KeyValuePair<TKey, TValue>[] right
    )
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var used = new bool[right.Length];
        var keyComparer = EqualityComparer<TKey>.Default;
        foreach (var leftPair in left)
        {
            var matched = false;
            for (var index = 0; index < right.Length; index++)
            {
                var rightPair = right[index];
                if (used[index] || !keyComparer.Equals(leftPair.Key, rightPair.Key))
                {
                    continue;
                }

                if (!AreEqual(leftPair.Value, rightPair.Value))
                {
                    continue;
                }

                used[index] = true;
                matched = true;
                break;
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }
}
