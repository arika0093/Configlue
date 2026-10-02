using SparseFragments;

namespace SparseFragments.Tests;

[SparseFragmentModel]
public partial class BuilderParent
{
    public BuilderChild? Child { get; set; }
    public string? Label { get; set; }
}

public class BuilderChild
{
    public int Count { get; set; }
}

public sealed class FragmentBuilderTests
{
    [Test]
    public void BuilderPreservesMissingAndPresentNullWithoutChangingOriginal()
    {
        var original = new BuilderParent.Fragment { Label = "before" };
        var builder = original.ToBuilder();
        builder.Label = Optional<string?>.Present(null);
        var result = builder.Build();
        result.Label.IsPresent.ShouldBeTrue();
        result.Label.Value.ShouldBeNull();
        result.Child.IsPresent.ShouldBeFalse();
        original.Label.Value.ShouldBe("before");
        builder.Label = Optional<string?>.Missing;
        builder.Build().Label.IsPresent.ShouldBeFalse();
        result.Label.IsPresent.ShouldBeTrue();
    }

    [Test]
    public void StructuralChildHasTypedBuilder()
    {
        var original = BuilderParent.Fragment.From(
            new BuilderParent { Child = new BuilderChild { Count = 7 } }
        );
        var parent = original.ToBuilder();
        var child = parent.Child.Value!.ToBuilder();
        child.Count = 9;
        parent.Child = child.Build();
        parent.Build().ToModel().Child!.Count.ShouldBe(9);
        original.ToModel().Child!.Count.ShouldBe(7);
    }
}
