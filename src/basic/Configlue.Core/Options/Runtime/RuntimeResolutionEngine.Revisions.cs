using Configlue.CompilerServices;

namespace Configlue;

internal sealed partial class RuntimeResolutionEngine<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private RevisionSnapshotCache? _revisionSnapshotCache;

    private StateRevisionVector CreateRevisionVector(
        StateSource<TFragment>[] sources,
        StateRevision[]? revisions,
        StateRevision singleRevision,
        int revisionCount,
        KeyValuePair<SourceId, StateRevisionVector>[]? nestedRevisions,
        int nestedRevisionCount
    )
    {
        var cache = Volatile.Read(ref _revisionSnapshotCache);
        if (cache is null || !ReferenceEquals(cache.Sources, sources))
        {
            // Topology changes invalidate the cache, including observable source order.
            // Concurrent reads may publish an older cache; this only costs a future miss.
            cache = new RevisionSnapshotCache(sources);
            Volatile.Write(ref _revisionSnapshotCache, cache);
        }
        return cache.GetOrCreate(
            revisions,
            singleRevision,
            revisionCount,
            nestedRevisions,
            nestedRevisionCount
        );
    }

    private sealed class RevisionSnapshotCache(StateSource<TFragment>[] sources)
    {
        private StateRevisionVector? _vector;
        internal StateSource<TFragment>[] Sources { get; } = sources;

        internal StateRevisionVector GetOrCreate(
            StateRevision[]? revisions,
            StateRevision singleRevision,
            int revisionCount,
            KeyValuePair<SourceId, StateRevisionVector>[]? nestedRevisions,
            int nestedRevisionCount
        )
        {
            var previous = Volatile.Read(ref _vector);
            if (previous is not null)
            {
                bool matches;
                if (revisions is null && revisionCount == 0)
                    matches = previous.Matches([], []);
                else if (revisions is null)
                    matches = previous.MatchesSingle(
                        singleRevision,
                        nestedRevisionCount > 0 ? nestedRevisions![0].Value : null
                    );
                else
                    matches = previous.Matches(
                        revisions.AsSpan(0, revisionCount),
                        nestedRevisions is null
                            ? default
                            : nestedRevisions.AsSpan(0, nestedRevisionCount)
                    );
                if (matches)
                    return previous;
            }

            // Never retain pooled scratch arrays. The factory owns immutable copies.
            var vector = RuntimeState.CreateRevisionVector(
                revisions,
                singleRevision,
                revisionCount,
                nestedRevisions,
                nestedRevisionCount
            );
            Volatile.Write(ref _vector, vector);
            return vector;
        }
    }
}
