namespace Configlue.State;

public sealed partial class StateRevisionVector
{
    // Runtime inputs come from a validated source set: each direct/nested source ID
    // is unique. Matching is only used within one immutable source-order snapshot.
    internal bool Matches(
        ReadOnlySpan<StateRevision> revisions,
        ReadOnlySpan<KeyValuePair<SourceId, StateRevisionVector>> nestedRevisions
    )
    {
        if (
            _revisions.Count != revisions.Length
            || _nestedRevisions.Count != nestedRevisions.Length
        )
            return false;
        // Nested identities and deeper source revisions often change while earlier
        // fallback observations remain stable. Reject those misses before scanning
        // unchanged direct entries. The cached source topology preserves ordering.
        foreach (var nested in nestedRevisions)
        {
            if (
                !TryGetNestedRevisions(nested.Key, out var previous)
                || !ReferenceEquals(previous, nested.Value)
            )
                return false;
        }
        for (var index = revisions.Length - 1; index >= 0; index--)
        {
            var revision = revisions[index];
            if (
                !TryGetRevision(revision.SourceId, out var previous)
                || !string.Equals(previous, revision.Revision, StringComparison.Ordinal)
            )
                return false;
        }
        return true;
    }

    internal bool MatchesSingle(StateRevision revision, StateRevisionVector? nested)
    {
        if (
            _revisions.Count != 1
            || !TryGetRevision(revision.SourceId, out var previous)
            || !string.Equals(previous, revision.Revision, StringComparison.Ordinal)
        )
            return false;
        if (nested is null)
            return _nestedRevisions.Count == 0;
        return _nestedRevisions.Count == 1
            && TryGetNestedRevisions(revision.SourceId, out var current)
            && ReferenceEquals(current, nested);
    }
}
