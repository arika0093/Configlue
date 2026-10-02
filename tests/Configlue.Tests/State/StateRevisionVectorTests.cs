namespace Configlue.Tests;

public sealed class StateRevisionVectorTests
{
    [Test]
    public void SingleEntryRevisionMapsExposeReadOnlyDictionaryBehavior()
    {
        var child = new StateRevisionVector([
            new StateRevision(SourceId.From("inner"), "revision-2"),
        ]);
        var vector = new StateRevisionVector(
            [new StateRevision(SourceId.From("source"), "revision-1")],
            [new KeyValuePair<SourceId, StateRevisionVector>(SourceId.From("composite"), child)]
        );

        vector.Revisions.Count.ShouldBe(1);
        vector.Revisions[SourceId.From("source")].ShouldBe("revision-1");
        vector.Revisions.Keys.Single().ShouldBe(SourceId.From("source"));
        vector.Revisions.Values.Single().ShouldBe("revision-1");
        vector
            .Revisions.Single()
            .ShouldBe(new KeyValuePair<SourceId, string?>(SourceId.From("source"), "revision-1"));
        vector.TryGetRevision(SourceId.From("missing"), out var missingRevision).ShouldBeFalse();
        missingRevision.ShouldBeNull();

        vector.NestedRevisions.Count.ShouldBe(1);
        vector.NestedRevisions[SourceId.From("composite")].ShouldBeSameAs(child);
        vector.NestedRevisions.Keys.Single().ShouldBe(SourceId.From("composite"));
        vector.NestedRevisions.Values.Single().ShouldBeSameAs(child);
        vector.TryGetNestedRevisions(SourceId.From("missing"), out var missingNested).ShouldBeFalse();
        missingNested.ShouldBeNull();
    }

    [Test]
    public void FromSpanBuildsRevisionMapsWithoutEnumeration()
    {
        var child = new StateRevisionVector([
            new StateRevision(SourceId.From("nested"), "revision-3"),
        ]);
        StateRevision[] revisions =
        [
            new(SourceId.From("first"), "revision-1"),
            new(SourceId.From("second"), null),
        ];
        KeyValuePair<SourceId, StateRevisionVector>[] nested =
        [
            new(SourceId.From("composite"), child),
            new(SourceId.From("other-composite"), child),
        ];

        var vector = StateRevisionVector.FromSpan(revisions, nested);

        vector.TryGetRevision(SourceId.From("second"), out var revision).ShouldBeTrue();
        revision.ShouldBeNull();
        vector.TryGetNestedRevisions(SourceId.From("composite"), out var nestedVector).ShouldBeTrue();
        nestedVector.ShouldBeSameAs(child);
        vector.NestedRevisions.Count.ShouldBe(2);
        vector.NestedRevisions.ContainsKey(SourceId.From("other-composite")).ShouldBeTrue();
    }

    [Test]
    public void FromSpanRejectsDuplicateKeys()
    {
        StateRevision[] duplicateRevisions =
        [
            new(SourceId.From("source"), "revision-1"),
            new(SourceId.From("source"), "revision-2"),
        ];
        KeyValuePair<SourceId, StateRevisionVector>[] duplicateNestedRevisions =
        [
            new(SourceId.From("composite"), new StateRevisionVector([])),
            new(SourceId.From("composite"), new StateRevisionVector([])),
        ];

        Should.Throw<ArgumentException>(() => StateRevisionVector.FromSpan(duplicateRevisions));
        Should.Throw<ArgumentException>(() =>
            StateRevisionVector.FromSpan([], duplicateNestedRevisions)
        );
    }
}
