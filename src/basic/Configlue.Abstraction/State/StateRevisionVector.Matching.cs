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
        var count = _hasSingleRevision ? 1 : _smallRevisions?.Length ?? _revisions?.Count ?? 0;
        var nestedCount = _hasSingleNestedRevision
            ? 1
            : _smallNestedRevisions?.Length ?? _nestedRevisions?.Count ?? 0;
        if (count != revisions.Length || nestedCount != nestedRevisions.Length)
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

    internal bool MatchesSingle(StateRevision revision, StateRevisionVector? nested) =>
        _hasSingleRevision
        && _singleRevisionSource == revision.SourceId
        && string.Equals(_singleRevision, revision.Revision, StringComparison.Ordinal)
        && (
            nested is null
                ? !_hasSingleNestedRevision
                    && _smallNestedRevisions is null
                    && (_nestedRevisions?.Count ?? 0) == 0
                : _hasSingleNestedRevision
                    && _singleNestedRevisionSource == revision.SourceId
                    && ReferenceEquals(_singleNestedRevision, nested)
        );
}
