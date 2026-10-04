using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.State;

// Follow-up #214 for #169: splits the combined
// StateRevisionVectorBenchmarks.ConstructAndLookup case into internal lookup
// versus public Revisions-view costs measured at identical entry counts.
// Since issue #276 the views are materialized eagerly at construction, so
// ViewMaterializeFirstAccess measures construction plus one view access and
// ViewCachedAccess measures steady-state access only.
[MemoryDiagnoser]
public class RevisionVectorViewBenchmarks214
{
    private StateRevision[] _revisions = [];
    private StateRevisionVector _lookupVector = null!;
    private StateRevisionVector _cachedVector = null!;
    private SourceId _hitId;
    private SourceId _missId;

    [Params(0, 1, 2, 4, 16)]
    public int EntryCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _revisions = new StateRevision[EntryCount];
        for (var index = 0; index < _revisions.Length; index++)
        {
            var sourceId = SourceId.From($"source-{index}");
            _revisions[index] = new StateRevision(sourceId, $"revision-{index}");
            _hitId = sourceId;
        }

        if (EntryCount == 0)
        {
            // No hit candidate exists; the "hit" benchmark exercises the same
            // empty-vector guard as the miss benchmark at this size.
            _hitId = SourceId.From("source-0");
        }

        _missId = SourceId.From("missing-source");
        _lookupVector = StateRevisionVector.FromSpan(_revisions);
        _cachedVector = StateRevisionVector.FromSpan(_revisions);

        // Pre-materialize so this benchmark measures steady-state cached
        // access only. Materialization itself is measured separately below.
        _ = _cachedVector.Revisions;
    }

    [Benchmark]
    public bool LookupHit() => _lookupVector.TryGetRevision(_hitId, out _);

    [Benchmark]
    public bool LookupMiss() => _lookupVector.TryGetRevision(_missId, out _);

    [Benchmark]
    public int ViewMaterializeFirstAccess()
    {
        // Views are eager since issue #276; construction is included because a
        // fresh vector cannot be reused across invocations. Compare against
        // ViewCachedAccess to isolate the construction cost.
        var vector = StateRevisionVector.FromSpan(_revisions);
        return vector.Revisions.Count;
    }

    [Benchmark]
    public int ViewCachedAccess() => _cachedVector.Revisions.Count;
}
