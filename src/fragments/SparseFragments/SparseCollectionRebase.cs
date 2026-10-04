using System.ComponentModel;

#if CONFIGLUE_FRAGMENT_RUNTIME
namespace Configlue;

#else
namespace SparseFragments;

#endif

/// <summary>Domain-neutral collection rebase rules shared by generated append and set-union members.</summary>
/// <remarks>Generated-code plumbing: referenced by emitted code, not hand-written callers.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
#if CONFIGLUE_FRAGMENT_RUNTIME
public static class ConfiglueCollectionRebase
#else
public static class SparseCollectionRebase
#endif
{
    /// <summary>
    /// Reapplies an append edit (a suffix of added elements) onto a newer collection, or reports why it cannot.
    /// </summary>
    /// <param name="before">The baseline collection.</param>
    /// <param name="desired">The locally edited collection.</param>
    /// <param name="current">The newer collection.</param>
    /// <param name="equal">Element equality.</param>
    /// <param name="rebased">The rebased collection on success.</param>
    /// <param name="reason">The failure reason on failure.</param>
    public static bool TryRebaseAppend(
        IReadOnlyList<object?> before,
        IReadOnlyList<object?> desired,
        IReadOnlyList<object?> current,
        Func<object?, object?, bool> equal,
        out IReadOnlyList<object?> rebased,
        out string? reason
    )
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(equal);

        if (SequenceEqual(current, desired, equal) || SequenceEqual(desired, before, equal))
        {
            rebased = current;
            reason = null;
            return true;
        }

        if (!HasPrefix(desired, before, equal))
        {
            if (!SequenceEqual(current, before, equal) && !SequenceEqual(current, desired, equal))
            {
                rebased = [];
                reason =
                    "The configuration edit conflicts with a concurrent change to an append-merged member.";
                return false;
            }

            rebased = desired;
            reason = null;
            return true;
        }

        if (!HasPrefix(current, before, equal))
        {
            rebased = [];
            reason =
                "The configuration edit cannot reapply its append because the existing collection prefix changed.";
            return false;
        }

        var result = new List<object?>(current);
        for (var index = before.Count; index < desired.Count; index++)
        {
            result.Add(desired[index]);
        }

        rebased = result;
        reason = null;
        return true;
    }

    /// <summary>Reapplies a set-union edit (added and removed elements) onto a newer collection.</summary>
    /// <param name="before">The baseline collection.</param>
    /// <param name="desired">The locally edited collection.</param>
    /// <param name="current">The newer collection.</param>
    /// <param name="equal">Element equality.</param>
    /// <param name="rebased">The rebased collection on success.</param>
    /// <param name="reason">The failure reason on failure.</param>
    public static bool TryRebaseSetUnion(
        IReadOnlyList<object?> before,
        IReadOnlyList<object?> desired,
        IReadOnlyList<object?> current,
        Func<object?, object?, bool> equal,
        out IReadOnlyList<object?> rebased,
        out string? reason
    )
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(equal);

        var removed = before
            .Where(value => !desired.Any(candidate => equal(candidate, value)))
            .ToArray();
        if (
            removed.Length > 0
            && !SequenceEqual(current, before, equal)
            && !SequenceEqual(current, desired, equal)
        )
        {
            rebased = [];
            reason =
                "The configuration edit conflicts with a concurrent change to a set-union member.";
            return false;
        }

        if (removed.Length > 0)
        {
            rebased = desired;
            reason = null;
            return true;
        }

        var result = new List<object?>(current);
        foreach (
            var value in desired
                .Where(value => !before.Any(candidate => equal(candidate, value)))
                .Where(value => !result.Any(candidate => equal(candidate, value)))
        )
        {
            result.Add(value);
        }

        rebased = result;
        reason = null;
        return true;
    }

    private static bool HasPrefix(
        IReadOnlyList<object?> candidate,
        IReadOnlyList<object?> prefix,
        Func<object?, object?, bool> equal
    )
    {
        if (candidate.Count < prefix.Count)
        {
            return false;
        }

        for (var index = 0; index < prefix.Count; index++)
        {
            if (!equal(candidate[index], prefix[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequenceEqual(
        IReadOnlyList<object?> left,
        IReadOnlyList<object?> right,
        Func<object?, object?, bool> equal
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!equal(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }
}
