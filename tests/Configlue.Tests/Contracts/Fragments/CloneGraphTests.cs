using Configlue;

namespace Configlue.Tests;

[ConfiglueModel("clone-graph")]
public partial class CloneGraphNode
{
    public int Value { get; set; }
    public CloneGraphNode? Next { get; set; }
    public CloneGraphNode? Peer { get; set; }
    public List<CloneGraphNode> Nodes { get; set; } = new();
    public Dictionary<CloneGraphNode, CloneGraphNode> Map { get; set; } = new();
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
        root.Nodes.Add(child);
        root.Nodes.Add(root);
        root.Map.Add(child, child);
        var clone = root.DeepClone();
        ReferenceEquals(clone, root).ShouldBeFalse();
        ReferenceEquals(clone.Next, child).ShouldBeFalse();
        ReferenceEquals(clone.Next, clone.Peer).ShouldBeTrue();
        ReferenceEquals(clone.Next!.Next, clone).ShouldBeTrue();
        ReferenceEquals(clone.Nodes[0], clone.Next).ShouldBeTrue();
        ReferenceEquals(clone.Nodes[1], clone).ShouldBeTrue();
        ReferenceEquals(clone.Map.Keys.Single(), clone.Next).ShouldBeTrue();
        ReferenceEquals(clone.Map.Values.Single(), clone.Next).ShouldBeTrue();
        ReferenceEquals(clone.Nodes, root.Nodes).ShouldBeFalse();
        ReferenceEquals(clone.Map, root.Map).ShouldBeFalse();
        clone.Next.Value = 9;
        child.Value.ShouldBe(7);
    }
}
