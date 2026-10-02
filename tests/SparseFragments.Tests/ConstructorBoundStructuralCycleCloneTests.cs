using SparseFragments;

namespace SparseFragments.Tests;

[SparseFragmentModel]
public partial class StructuralCycleRoot
{
    public StructuralCycleChild Child { get; set; } = new("default");
}

public sealed class StructuralCycleChild(string name)
{
    public string Name { get; } = name;
    public StructuralCycleChild? Next { get; set; }
}

public sealed class ConstructorBoundStructuralCycleCloneTests
{
    [Test]
    public void ConstructorBoundStructuralNodePreservesCycleThroughSetterMember()
    {
        var child = new StructuralCycleChild("child");
        child.Next = child;
        var clone = new StructuralCycleRoot { Child = child }.DeepClone();

        ReferenceEquals(clone.Child, child).ShouldBeFalse();
        clone.Child.Name.ShouldBe("child");
        ReferenceEquals(clone.Child.Next, clone.Child).ShouldBeTrue();
    }
}
