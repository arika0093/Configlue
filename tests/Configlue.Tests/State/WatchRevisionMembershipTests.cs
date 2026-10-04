using Configlue.State;

namespace Configlue.Tests;

public sealed class WatchRevisionMembershipTests
{
    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(4, false)]
    [Arguments(16, false)]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(2, true)]
    [Arguments(4, true)]
    [Arguments(16, true)]
    public void MembershipAllowsExtraActiveSourcesAndRejectsRetiredSources(
        int count,
        bool enumerable
    )
    {
        var entries = Enumerable
            .Range(0, count)
            .Select(index => new StateRevision(SourceId.From($"source-{index}"), null))
            .ToArray();
        var vector = enumerable
            ? new StateRevisionVector(entries)
            : StateRevisionVector.FromSpan(entries);
        var active = entries.Select(entry => entry.SourceId).ToHashSet();
        active.Add(SourceId.From("extra"));
        var firstAllocation = GC.GetAllocatedBytesForCurrentThread();
        var firstMatch = vector.ContainsOnlySources(active);
        firstAllocation = GC.GetAllocatedBytesForCurrentThread() - firstAllocation;
        firstMatch.ShouldBeTrue();
        firstAllocation.ShouldBe(0);
        if (count > 0)
        {
            active.Remove(entries[^1].SourceId);
            vector.ContainsOnlySources(active).ShouldBeFalse();
            active.Add(entries[^1].SourceId);
        }

        for (var index = 0; index < 100; index++)
            _ = vector.ContainsOnlySources(active);
        var matched = true;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
            matched &= vector.ContainsOnlySources(active);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        matched.ShouldBeTrue();
        allocated.ShouldBe(0);
        active.Clear();
        vector.ContainsOnlySources(active).ShouldBe(count == 0);
    }

    [Test]
    public void NestedSourceIdsDoNotParticipateInDirectMembership()
    {
        var child = StateRevisionVector.FromSingle(new(SourceId.From("child"), "revision"));
        var vector = StateRevisionVector.FromSpan([], [new(SourceId.From("outer"), child)]);
        vector.ContainsOnlySources([]).ShouldBeTrue();
    }

    [Test]
    public void CustomSetComparerKeepsSetMembershipSemantics()
    {
        var entries = Enumerable
            .Range(0, 16)
            .Select(index => new StateRevision(SourceId.From($"source-{index}"), null))
            .ToArray();
        var vector = StateRevisionVector.FromSpan(entries);
        var active = new HashSet<SourceId>(new AllSourcesComparer())
        {
            SourceId.From("equivalent"),
        };
        vector.ContainsOnlySources(active).ShouldBeTrue();
        active.Clear();
        vector.ContainsOnlySources(active).ShouldBeFalse();
    }

    private sealed class AllSourcesComparer : IEqualityComparer<SourceId>
    {
        public bool Equals(SourceId x, SourceId y) => true;

        public int GetHashCode(SourceId obj) => 0;
    }
}
