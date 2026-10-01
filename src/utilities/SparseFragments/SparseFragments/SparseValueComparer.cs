using System.Collections;

namespace SparseFragments;

/// <summary>Default semantic equality used by generated sparse fragments.</summary>
public static class SparseValueComparer
{
    /// <summary>Compares two values, treating sequences and sets element-wise.</summary>
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

    /// <summary>Compares two typed values element-wise when they are collections.</summary>
    public static bool AreEqual<T>(T? left, T? right) => AreEqual((object?)left, (object?)right);
}
