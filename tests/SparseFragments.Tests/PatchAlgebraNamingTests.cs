namespace SparseFragments.Tests;

[SparseFragmentModel]
public partial class AlgebraNamedRoot
{
    public int Compose { get; set; }
    public int @class { get; set; }
    public AlgebraNamedChild? Child { get; set; }
}

[SparseFragmentModel]
public partial class AlgebraNamedChild
{
    public int Compose { get; set; }
}

public sealed class PatchAlgebraNamingTests
{
    [Test]
    public void AlgebraApisCoexistWithModelMembersAndKeywordIdentifiers()
    {
        var before = Optional<AlgebraNamedRoot.Fragment?>.Present(
            AlgebraNamedRoot.Fragment.From(
                new AlgebraNamedRoot
                {
                    Compose = 1,
                    @class = 2,
                    Child = new AlgebraNamedChild { Compose = 3 },
                }
            )
        );
        var after = Optional<AlgebraNamedRoot.Fragment?>.Present(
            AlgebraNamedRoot.Fragment.From(
                new AlgebraNamedRoot
                {
                    Compose = 4,
                    @class = 5,
                    Child = new AlgebraNamedChild { Compose = 6 },
                }
            )
        );
        var patch = AlgebraNamedRoot.Patch.SparseBetween(before, after);
        var composed = patch.SparseCompose(new AlgebraNamedRoot.Patch { Compose = 8 });
        var rebased = AlgebraNamedRoot.Patch.SparseRebase(before, composed, before);
        rebased.HasConflicts.ShouldBeFalse();
        var result = Apply(rebased.Patch, before).Value!;
        result.Compose.Value.ShouldBe(8);
        result.@class.Value.ShouldBe(5);
        result.Child.Value!.Compose.Value.ShouldBe(6);
        var restored = Apply(composed.SparseInvert(before), Apply(composed, before)).Value!;
        restored.Compose.Value.ShouldBe(1);
        restored.@class.Value.ShouldBe(2);
        restored.Child.Value!.Compose.Value.ShouldBe(3);
        // Rebase and composition do not consume or mutate the original transition.
        Apply(patch, before).Value!.Compose.Value.ShouldBe(4);
        Apply(rebased.Patch, before).Value!.Compose.Value.ShouldBe(8);
    }

    private static Optional<AlgebraNamedRoot.Fragment?> Apply(
        AlgebraNamedRoot.Patch patch,
        Optional<AlgebraNamedRoot.Fragment?> state
    ) => ((ISparseModelPatch<AlgebraNamedRoot, AlgebraNamedRoot.Fragment>)patch).Apply(state);
}
