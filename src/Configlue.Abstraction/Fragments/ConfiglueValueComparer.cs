using System.Collections;

namespace Configlue;

/// <summary>Compares model values for generated semantic diffs.</summary>
public static class ConfiglueValueComparer
{
    /// <summary>Compares ordinary values and enumerable members by their contents.</summary>
    public static bool AreEqual<T>(T? left, T? right)
    {
        if (EqualityComparer<T?>.Default.Equals(left, right))
        {
            return true;
        }

        if (
            left is not IEnumerable leftItems
            || right is not IEnumerable rightItems
            || left is string
            || right is string
        )
        {
            return false;
        }

        if (IsSetType(typeof(T)))
        {
            var values = new HashSet<object?>(leftItems.Cast<object?>());
            return values.SetEquals(rightItems.Cast<object?>());
        }

        var leftEnumerator = leftItems.GetEnumerator();
        var rightEnumerator = rightItems.GetEnumerator();
        try
        {
            while (true)
            {
                var hasLeft = leftEnumerator.MoveNext();
                var hasRight = rightEnumerator.MoveNext();
                if (hasLeft != hasRight)
                {
                    return false;
                }

                if (!hasLeft)
                {
                    return true;
                }

                if (!Equals(leftEnumerator.Current, rightEnumerator.Current))
                {
                    return false;
                }
            }
        }
        finally
        {
            (leftEnumerator as IDisposable)?.Dispose();
            (rightEnumerator as IDisposable)?.Dispose();
        }
    }

    private static bool IsSetType(Type type)
    {
        if (!type.IsGenericType)
        {
            return false;
        }

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(HashSet<>)
            || definition == typeof(ISet<>)
            || definition == typeof(IReadOnlySet<>);
    }
}
