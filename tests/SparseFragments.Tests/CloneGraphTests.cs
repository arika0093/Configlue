using SparseFragments;

namespace SparseFragments.Tests;

[SparseFragmentModel]
public partial class CloneGraphNode
{
    public int Value { get; set; }
    public CloneGraphNode? Next { get; set; }
    public CloneGraphNode? Peer { get; set; }
}

public sealed class CloneGraphTests
{
    [Test]
    public void GeneratedModelClonePreservesAliasesAndParentCycles()
    {
        var root = new CloneGraphNode();
        var child = new CloneGraphNode { Value = 7, Next = root };
        root.Next = child;
        root.Peer = child;
        var clone = root.DeepClone();
        ReferenceEquals(clone, root).ShouldBeFalse();
        ReferenceEquals(clone.Next, child).ShouldBeFalse();
        ReferenceEquals(clone.Next, clone.Peer).ShouldBeTrue();
        ReferenceEquals(clone.Next!.Next, clone).ShouldBeTrue();
        clone.Next.Value = 9;
        child.Value.ShouldBe(7);
    }
}
