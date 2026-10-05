using Configlue.State;

namespace Configlue.Tests;

[ConfiglueModel("runtime-revision-snapshots", Version = 1)]
public partial class RuntimeRevisionSnapshotSettings
{
    public int Counter { get; set; }
}

public sealed class RuntimeRevisionSnapshotTests
{
    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(16)]
    public async Task StableMetadataIsReusedWhileEverySourceAndValueRemainFresh(int count)
    {
        var (engine, _, readers, _, _) = Create(count);
        var first = await engine.ResolveAsync(null, CancellationToken.None);
        readers[^1].Result = Success(43, "stable");
        var second = await engine.ResolveAsync(
            null,
            CancellationToken.None,
            captureContributions: true
        );
        second.Result.Value!.Counter.ShouldBe(43);
        second.Result.Revisions.ShouldBeSameAs(first.Result.Revisions);
        second.Contributions.Count.ShouldBe(2);
        foreach (var reader in readers)
            reader.ReadCount.ShouldBe(2);
        readers[^1].Result = Success(44, "changed");
        var changed = await engine.ResolveAsync(null, CancellationToken.None);
        changed.Result.Revisions.ShouldNotBeSameAs(first.Result.Revisions);
        changed.Result.Value!.Counter.ShouldBe(44);
        first.Result.Value!.Counter.ShouldBe(42);
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public async Task NestedChangesAndRemovalPreservePreviouslyEscapedVectors(int count)
    {
        var (engine, _, readers, sources, _) = Create(count);
        var childA = StateRevisionVector.FromSingle(new(SourceId.From("child"), "a"));
        var childB = StateRevisionVector.FromSingle(new(SourceId.From("child"), "b"));
        readers[^1].Result = readers[^1].Result with { Revisions = childA };
        var first = await engine.ResolveAsync(null, CancellationToken.None);
        var stable = await engine.ResolveAsync(null, CancellationToken.None);
        stable.Result.Revisions.ShouldBeSameAs(first.Result.Revisions);
        readers[^1].Result = readers[^1].Result with { Revisions = childB };
        var changed = await engine.ResolveAsync(null, CancellationToken.None);
        changed.Result.Revisions.ShouldNotBeSameAs(first.Result.Revisions);
        changed
            .Result.Revisions!.TryGetNestedRevisions(sources[^1].Id, out var current)
            .ShouldBeTrue();
        current.ShouldBeSameAs(childB);
        first
            .Result.Revisions!.TryGetNestedRevisions(sources[^1].Id, out var original)
            .ShouldBeTrue();
        original.ShouldBeSameAs(childA);
        readers[^1].Result = readers[^1].Result with { Revisions = null };
        var removed = await engine.ResolveAsync(null, CancellationToken.None);
        removed.Result.Revisions!.TryGetNestedRevisions(sources[^1].Id, out _).ShouldBeFalse();
        removed.Result.Revisions.ShouldNotBeSameAs(changed.Result.Revisions);
    }

    [Test]
    public async Task RetirementChangesMembershipWithoutMutatingOldVector()
    {
        var (engine, topology, _, sources, _) = Create(4);
        var first = await engine.ResolveAsync(null, CancellationToken.None);
        topology.RetireSources([sources[0].Id]).ShouldNotBeNull();
        var second = await engine.ResolveAsync(null, CancellationToken.None);
        second.Result.Revisions.ShouldNotBeSameAs(first.Result.Revisions);
        second.Result.Revisions!.Revisions.Count.ShouldBe(3);
        first.Result.Revisions!.Revisions.Count.ShouldBe(4);
        second.Result.Value!.Counter.ShouldBe(42);
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public async Task ReadStatusesAreNotPartOfTheMetadataCache(int count)
    {
        var (engine, _, readers, _, _) = Create(count);
        var first = await engine.ResolveAsync(null, CancellationToken.None);
        readers[^1].Result = StateReadResult<RuntimeRevisionSnapshotSettings.Fragment>.Unavailable(
            "stable"
        );
        var unavailable = await engine.ResolveAsync(null, CancellationToken.None);
        unavailable.Result.Status.ShouldBe(StateReadStatus.Unavailable);
        unavailable.Result.Value.ShouldBeNull();
        unavailable.Result.Revisions.ShouldBeSameAs(first.Result.Revisions);
        readers[^1].Result = Success(45, "stable");
        var recovered = await engine.ResolveAsync(null, CancellationToken.None);
        recovered.Result.Value!.Counter.ShouldBe(45);
        recovered.Result.Revisions.ShouldBeSameAs(first.Result.Revisions);
    }

    [Test]
    public async Task ProposalReplacementDoesNotCacheItsValue()
    {
        var (engine, _, readers, sources, _) = Create(4);
        var first = await engine.ResolveAsync(null, CancellationToken.None);
        var proposal = await engine.ResolveAsync(
            new Dictionary<SourceId, StateReadResult<RuntimeRevisionSnapshotSettings.Fragment>>
            {
                [sources[^1].Id] = Success(77, "stable"),
            },
            CancellationToken.None
        );
        proposal.Result.Value!.Counter.ShouldBe(77);
        proposal.Result.Revisions.ShouldBeSameAs(first.Result.Revisions);
        readers[^1].ReadCount.ShouldBe(1);
        (await engine.ResolveAsync(null, CancellationToken.None)).Result.Value!.Counter.ShouldBe(
            42
        );
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentSubjectReadsKeepTheirOwnValuesAndRevisionIdentities(
        bool sameRevision
    )
    {
        var reader = new SubjectReader(sameRevision);
        var (engine, _, _, sources, subjects) = Create(1, reader);
        await Task.WhenAll(
            Enumerable
                .Range(1, 2)
                .Select(marker =>
                    Task.Run(async () =>
                    {
                        using var scope = subjects.Enter(new Subject(marker));
                        for (var index = 0; index < 100; index++)
                        {
                            var result = await engine.ResolveAsync(null, CancellationToken.None);
                            result.Result.Value!.Counter.ShouldBe(marker);
                            result
                                .Result.Revisions!.TryGetRevision(sources[0].Id, out var revision)
                                .ShouldBeTrue();
                            revision.ShouldBe(sameRevision ? "same" : marker.ToString());
                        }
                    })
                )
        );
    }

    private static StateReadResult<RuntimeRevisionSnapshotSettings.Fragment> Success(
        int value,
        string revision
    ) =>
        StateReadResult<RuntimeRevisionSnapshotSettings.Fragment>.Success(
            new() { Counter = value },
            revision
        );

    private static (
        RuntimeResolutionEngine<
            RuntimeRevisionSnapshotSettings,
            RuntimeRevisionSnapshotSettings.Fragment
        > Engine,
        RuntimeSourceTopology<RuntimeRevisionSnapshotSettings.Fragment> Topology,
        Reader[] Readers,
        StateSource<RuntimeRevisionSnapshotSettings.Fragment>[] Sources,
        RuntimeSubjectContext Subjects
    ) Create(
        int count,
        ISourceReader<RuntimeRevisionSnapshotSettings.Fragment>? readerOverride = null
    )
    {
        var readers = Enumerable
            .Range(0, count)
            .Select(index => new Reader(
                index == count - 1
                    ? Success(42, "stable")
                    : StateReadResult<RuntimeRevisionSnapshotSettings.Fragment>.NotFound("missing")
            ))
            .ToArray();
        var sources = readers
            .Select(
                (reader, index) =>
                    new StateSource<RuntimeRevisionSnapshotSettings.Fragment>(
                        $"source-{index}",
                        readerOverride ?? reader,
                        new StateSourceOptions<RuntimeRevisionSnapshotSettings.Fragment>
                        {
                            Priority = count - index,
                            FallbackCondition =
                                StateFallbackCondition.NotFound
                                | StateFallbackCondition.Unavailable,
                        }
                    )
            )
            .ToArray();
        var topology = new RuntimeSourceTopology<RuntimeRevisionSnapshotSettings.Fragment>(
            new(sources),
            "runtime-revision-snapshots"
        );
        var subjects = new RuntimeSubjectContext();
        var diagnostics = new RuntimeDiagnosticRecorder(
            "snapshots",
            "runtime-revision-snapshots",
            1,
            ConfiglueRuntimeDiagnosticOptions.Disabled,
            []
        );
        var validation = new RuntimeValidationPipeline<
            RuntimeRevisionSnapshotSettings,
            RuntimeRevisionSnapshotSettings.Fragment
        >([], false, "snapshots", diagnostics);
        return (
            new(
                topology,
                subjects,
                diagnostics,
                new RuntimeLifetime(),
                validation,
                new RuntimeModelCloner<
                    RuntimeRevisionSnapshotSettings,
                    RuntimeRevisionSnapshotSettings.Fragment
                >(null),
                null
            ),
            topology,
            readers,
            sources,
            subjects
        );
    }

    private sealed class Reader(StateReadResult<RuntimeRevisionSnapshotSettings.Fragment> result)
        : ISourceReader<RuntimeRevisionSnapshotSettings.Fragment>
    {
        public StateReadResult<RuntimeRevisionSnapshotSettings.Fragment> Result { get; set; } =
            result;
        public int ReadCount { get; private set; }

        public ValueTask<StateReadResult<RuntimeRevisionSnapshotSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            ReadCount++;
            return new(Result);
        }
    }

    private sealed class Subject(int marker) : IConfiglueSubject
    {
        public int Marker { get; } = marker;
        public SubjectKey Key => SubjectKey.From(Marker.ToString());
    }

    private sealed class SubjectReader(bool sameRevision)
        : ISourceReader<RuntimeRevisionSnapshotSettings.Fragment>
    {
        private readonly StateReadResult<RuntimeRevisionSnapshotSettings.Fragment> _first = Success(
            1,
            sameRevision ? "same" : "1"
        );
        private readonly StateReadResult<RuntimeRevisionSnapshotSettings.Fragment> _second =
            Success(2, sameRevision ? "same" : "2");

        public ValueTask<StateReadResult<RuntimeRevisionSnapshotSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => new(((Subject)context.Subject).Marker == 1 ? _first : _second);
    }
}
