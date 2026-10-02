using SparseFragments;

namespace SparseFragments.Tests;

[SparseFragmentModel]
public partial class FragmentCloneGraphParent
{
    public FragmentCloneGraphChild? Left { get; set; }
    public FragmentCloneGraphChild? Right { get; set; }
    public FragmentCloneGraphChild? LeftAlias { get; set; }
}

[SparseFragmentModel]
public partial class FragmentCloneGraphChild
{
    public List<int> Values { get; set; } = new();
}

public sealed class FragmentCloneGraphTests
{
    [Test]
    public void FragmentCloneSharesItsContextAcrossNestedFragments()
    {
        var values = new List<int> { 1, 2 };
        var left = new FragmentCloneGraphChild.Fragment
        {
            Values = Optional<List<int>>.Present(values),
        };
        var right = new FragmentCloneGraphChild.Fragment
        {
            Values = Optional<List<int>>.Present(values),
        };
        var source = new FragmentCloneGraphParent.Fragment
        {
            Left = Optional<FragmentCloneGraphChild.Fragment?>.Present(left),
            Right = Optional<FragmentCloneGraphChild.Fragment?>.Present(right),
            LeftAlias = Optional<FragmentCloneGraphChild.Fragment?>.Present(left),
        };

        var clone = source.DeepClone();

        ReferenceEquals(clone.Left.Value, clone.LeftAlias.Value).ShouldBeTrue();
        ReferenceEquals(clone.Left.Value, left).ShouldBeFalse();
        ReferenceEquals(clone.Right.Value, right).ShouldBeFalse();
        ReferenceEquals(clone.Left.Value!.Values.Value, clone.Right.Value!.Values.Value)
            .ShouldBeTrue();
        ReferenceEquals(clone.Left.Value!.Values.Value, values).ShouldBeFalse();
        clone.Left.Value!.Values.Value.ShouldBe([1, 2]);
    }
}
