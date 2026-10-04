using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.State;

[MemoryDiagnoser]
public class WatchRevisionMembershipBenchmarks
{
    private StateRevisionVector _vector = null!;
    private HashSet<SourceId> _active = null!;

    [Params(0, 1, 4, 16)]
    public int RevisionCount { get; set; }

    [Params(false, true)]
    public bool HasRetiredSource { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var revisions = Enumerable
            .Range(0, RevisionCount)
            .Select(index => new StateRevision(SourceId.From($"source-{index}"), null))
            .ToArray();
        _vector = StateRevisionVector.FromSpan(revisions);
        _active = revisions.Select(revision => revision.SourceId).ToHashSet();
        _active.Add(SourceId.From("extra-active-source"));
        if (HasRetiredSource && revisions.Length > 0)
            _active.Remove(revisions[^1].SourceId);
        _ = CheckMembership();
    }

    [Benchmark]
    public bool CheckMembership() => !_vector.ContainsOnlySources(_active);
}
