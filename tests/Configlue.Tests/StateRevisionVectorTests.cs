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
}
