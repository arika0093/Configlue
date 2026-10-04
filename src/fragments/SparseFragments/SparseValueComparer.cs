using System.Collections;
using System.ComponentModel;

#if CONFIGLUE_FRAGMENT_RUNTIME
namespace Configlue;

#else
namespace SparseFragments;

#endif

/// <summary>Default semantic equality used by generated sparse fragments.</summary>
/// <remarks>Generated-code plumbing: referenced by emitted code, not hand-written callers.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
#if CONFIGLUE_FRAGMENT_RUNTIME
public static class ConfiglueValueComparer
#else
public static class SparseValueComparer
#endif
{
    /// <summary>Compares two values, treating ordinary sequences element-wise.</summary>
    public static bool AreEqual(object? left, object? right) =>
        FragmentComparisonPrimitives.AreValuesEqual(left, right);

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

        if (
            left is IReadOnlyDictionary<TKey, TValue> readOnlyDictionary
            && right is ICollection<KeyValuePair<TKey, TValue>> rightSized
            && readOnlyDictionary.Count == rightSized.Count
        )
        {
            foreach (var pair in right)
            {
                if (
                    !readOnlyDictionary.TryGetValue(pair.Key, out var value)
                    || !AreEqual(value, pair.Value)
                )
                {
                    return false;
                }
            }

            return true;
        }

        if (
            left is IDictionary<TKey, TValue> dictionary
            && right is ICollection<KeyValuePair<TKey, TValue>> rightEntries
            && dictionary.Count == rightEntries.Count
        )
        {
            foreach (var pair in right)
            {
                if (
                    !dictionary.TryGetValue(pair.Key, out var value) || !AreEqual(value, pair.Value)
                )
                {
                    return false;
                }
            }

            return true;
        }

        var rightMaterialized = right.ToArray();
        if (left is IReadOnlyDictionary<TKey, TValue> readOnlyLeft)
        {
            return DictionaryEquals(readOnlyLeft, rightMaterialized);
        }

        if (left is IDictionary<TKey, TValue> dictionaryLeft)
        {
            return DictionaryEquals(dictionaryLeft, rightMaterialized);
        }

        var leftEntries = left.ToArray();
        return PairSequenceEquals(leftEntries, rightMaterialized);
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
