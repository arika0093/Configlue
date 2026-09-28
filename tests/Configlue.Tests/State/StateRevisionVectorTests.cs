namespace Configlue.Tests;

public sealed class StateRevisionVectorTests
{
    [Test]
    public void SingleEntryRevisionMapsExposeReadOnlyDictionaryBehavior()
    {
        var child = new StateRevisionVector([new StateRevision("inner", "revision-2")]);
        var vector = new StateRevisionVector(
            [new StateRevision("source", "revision-1")],
            [new KeyValuePair<string, StateRevisionVector>("composite", child)]
        );

        vector.Revisions.Count.ShouldBe(1);
        vector.Revisions["source"].ShouldBe("revision-1");
        vector.Revisions.Keys.Single().ShouldBe("source");
        vector.Revisions.Values.Single().ShouldBe("revision-1");
        vector
            .Revisions.Single()
            .ShouldBe(new KeyValuePair<string, string?>("source", "revision-1"));
        vector.TryGetRevision("missing", out var missingRevision).ShouldBeFalse();
        missingRevision.ShouldBeNull();

        vector.NestedRevisions.Count.ShouldBe(1);
        vector.NestedRevisions["composite"].ShouldBeSameAs(child);
        vector.NestedRevisions.Keys.Single().ShouldBe("composite");
        vector.NestedRevisions.Values.Single().ShouldBeSameAs(child);
        vector.TryGetNestedRevisions("missing", out var missingNested).ShouldBeFalse();
        missingNested.ShouldBeNull();
    }

    [Test]
    public void FromSpanBuildsRevisionMapsWithoutEnumeration()
    {
        var child = new StateRevisionVector([new StateRevision("nested", "revision-3")]);
        StateRevision[] revisions = [new("first", "revision-1"), new("second", null)];
        KeyValuePair<string, StateRevisionVector>[] nested =
        [
            new("composite", child),
            new("other-composite", child),
        ];

        var vector = StateRevisionVector.FromSpan(revisions, nested);

        vector.TryGetRevision("second", out var revision).ShouldBeTrue();
        revision.ShouldBeNull();
        vector.TryGetNestedRevisions("composite", out var nestedVector).ShouldBeTrue();
        nestedVector.ShouldBeSameAs(child);
        vector.NestedRevisions.Count.ShouldBe(2);
        vector.NestedRevisions.ContainsKey("other-composite").ShouldBeTrue();
    }

    [Test]
    public void FromSpanRejectsDuplicateKeys()
    {
        StateRevision[] duplicateRevisions =
        [
            new("source", "revision-1"),
            new("source", "revision-2"),
        ];
        KeyValuePair<string, StateRevisionVector>[] duplicateNestedRevisions =
        [
            new("composite", new StateRevisionVector([])),
            new("composite", new StateRevisionVector([])),
        ];

        Should.Throw<ArgumentException>(() => StateRevisionVector.FromSpan(duplicateRevisions));
        Should.Throw<ArgumentException>(() =>
            StateRevisionVector.FromSpan([], duplicateNestedRevisions)
        );
    }
}
