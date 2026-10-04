using Configlue.State;

namespace Configlue.Tests;

public sealed class ResolverSnapshotReuseTests
{
    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(16)]
    public async Task StableRevisionReusesIdentityButStillReadsEverySource(int count)
    {
        var (resolver, readers, sources) = Create(count);
        var first = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        readers[^1].Result = StateReadResult<int>.Success(43, "stable");
        var second = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        second.Value.ShouldBe(43);
        second.Revisions.ShouldBeSameAs(first.Revisions);
        foreach (var reader in readers)
            reader.ReadCount.ShouldBe(2);

        using var oldWatch = resolver.GetSourcesForWatch("fallback");
        readers[^1].Result = StateReadResult<int>.Success(44, "changed");
        var changed = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        changed.Value.ShouldBe(44);
        changed.Revisions.ShouldNotBeSameAs(first.Revisions);
        first.Revisions!.TryGetRevision(sources[^1].Id, out var oldRevision).ShouldBeTrue();
        oldRevision.ShouldBe("stable");
        changed.Revisions!.TryGetRevision(sources[^1].Id, out var currentRevision).ShouldBeTrue();
        currentRevision.ShouldBe("changed");
        oldWatch.Targets[^1].ObservedRevision.ShouldBe("stable");
        using var currentWatch = resolver.GetSourcesForWatch("fallback");
        currentWatch.Targets[^1].ObservedRevision.ShouldBe("changed");
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public async Task NestedRevisionChangesAreObservedEvenWithUnchangedOuterRevision(int count)
    {
        var (resolver, readers, sources) = Create(count);
        var childA = StateRevisionVector.FromSingle(new(SourceId.From("child"), "a"));
        var childB = StateRevisionVector.FromSingle(new(SourceId.From("child"), "b"));
        readers[^1].Result = readers[^1].Result with { Revisions = childA };
        var first = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        var repeated = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        repeated.Revisions.ShouldBeSameAs(first.Revisions);

        readers[^1].Result = readers[^1].Result with { Revisions = childB };
        var changed = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        changed.Revisions.ShouldNotBeSameAs(first.Revisions);
        changed
            .Revisions!.TryGetNestedRevisions(sources[^1].Id, out var currentChild)
            .ShouldBeTrue();
        currentChild.ShouldBeSameAs(childB);
        first.Revisions!.TryGetNestedRevisions(sources[^1].Id, out var oldChild).ShouldBeTrue();
        oldChild.ShouldBeSameAs(childA);

        readers[^1].Result = readers[^1].Result with { Revisions = null };
        var removed = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        removed.Revisions.ShouldNotBeSameAs(changed.Revisions);
        removed.Revisions!.TryGetNestedRevisions(sources[^1].Id, out _).ShouldBeFalse();
        var removedRepeat = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        removedRepeat.Revisions.ShouldBeSameAs(removed.Revisions);
    }

    [Test]
    public async Task NestedRevisionFromMissingSourceIsNotLost()
    {
        var (resolver, readers, sources) = Create(2);
        var child = StateRevisionVector.FromSingle(new(SourceId.From("child"), "new"));
        var first = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        readers[0].Result = readers[0].Result with { Revisions = child };
        var changed = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        changed.Revisions.ShouldNotBeSameAs(first.Revisions);
        changed.Revisions!.TryGetNestedRevisions(sources[0].Id, out var nested).ShouldBeTrue();
        nested.ShouldBeSameAs(child);
    }

    [Test]
    public async Task FailoverRebuildsTheParticipatingSourceSnapshot()
    {
        var (resolver, readers, sources) = Create(4);
        var first = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        readers[0].Result = StateReadResult<int>.Success(7, "stable");
        var changed = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        changed.Value.ShouldBe(7);
        changed.Revisions!.Revisions.Count.ShouldBe(1);
        first.Revisions!.Revisions.Count.ShouldBe(4);
        resolver.ActiveSource.ShouldBeSameAs(sources[0]);
        using var watch = resolver.GetSourcesForWatch("fallback");
        watch.Targets.Count.ShouldBe(1);
        watch.Targets[0].Source.ShouldBeSameAs(sources[0]);
    }

    [Test]
    public async Task ReadStatusAndActiveSourceAreNeverCachedAsValues()
    {
        var (resolver, readers, sources) = Create(1);
        await resolver.ReadAsync(ConfiglueResourceContext.Default);
        readers[0].Result = StateReadResult<int>.NotFound("stable");
        (await resolver.ReadAsync(ConfiglueResourceContext.Default)).Status.ShouldBe(
            StateReadStatus.NotFound
        );
        resolver.ActiveSource.ShouldBeNull();
        readers[0].Result = StateReadResult<int>.Unavailable("stable");
        (await resolver.ReadAsync(ConfiglueResourceContext.Default)).Status.ShouldBe(
            StateReadStatus.Unavailable
        );
        resolver.ActiveSource.ShouldBeNull();
        readers[0].Result = StateReadResult<int>.Success(45, "stable");
        (await resolver.ReadAsync(ConfiglueResourceContext.Default)).Value.ShouldBe(45);
        resolver.ActiveSource.ShouldBeSameAs(sources[0]);
    }

    private static (
        StateSourceResolver<int> Resolver,
        MutableReader[] Readers,
        StateSource<int>[] Sources
    ) Create(int count)
    {
        var readers = Enumerable
            .Range(0, count)
            .Select(index => new MutableReader(
                index == count - 1
                    ? StateReadResult<int>.Success(42, "stable")
                    : StateReadResult<int>.NotFound("missing")
            ))
            .ToArray();
        var sources = readers
            .Select(
                (reader, index) =>
                    new StateSource<int>(
                        $"source-{index}",
                        reader,
                        new StateSourceOptions<int>
                        {
                            Priority = count - index,
                            FallbackCondition =
                                StateFallbackCondition.NotFound
                                | StateFallbackCondition.Unavailable,
                        }
                    )
            )
            .ToArray();
        return (new StateSourceResolver<int>(new(sources)), readers, sources);
    }

    private sealed class MutableReader(StateReadResult<int> result) : ISourceReader<int>
    {
        public StateReadResult<int> Result { get; set; } = result;
        public int ReadCount { get; private set; }

        public ValueTask<StateReadResult<int>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            ReadCount++;
            return new(Result);
        }
    }
}
