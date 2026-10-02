using Configlue;

namespace Configlue.Tests;

[ConfiglueModel("clone-graph")]
public partial class CloneGraphNode
{
    public int Value { get; set; }
    public ISet<CloneGraphNode> Members { get; set; } = new HashSet<CloneGraphNode>();
    public CloneGraphNode[] Buffer { get; set; } = Array.Empty<CloneGraphNode>();
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
        root.Members.Add(child);
        root.Members.Add(root);
        child.Members = root.Members;
        root.Buffer = new[] { child, root };
        child.Buffer = root.Buffer;
        root.Nodes.Add(child);
        root.Nodes.Add(root);
        child.Nodes = root.Nodes;
        root.Map.Add(child, child);
        child.Map = root.Map;
        var clone = root.DeepClone();
        ReferenceEquals(clone, root).ShouldBeFalse();
        ReferenceEquals(clone.Next, child).ShouldBeFalse();
        ReferenceEquals(clone.Next, clone.Peer).ShouldBeTrue();
        ReferenceEquals(clone.Next!.Members, clone.Members).ShouldBeTrue();
        clone.Members.Contains(clone.Next).ShouldBeTrue();
        clone.Members.Contains(clone).ShouldBeTrue();
        ReferenceEquals(clone.Buffer, root.Buffer).ShouldBeFalse();
        ReferenceEquals(clone.Buffer[0], clone.Next).ShouldBeTrue();
        ReferenceEquals(clone.Buffer[1], clone).ShouldBeTrue();
        ReferenceEquals(clone.Next!.Buffer, clone.Buffer).ShouldBeTrue();
        ReferenceEquals(clone.Next!.Next, clone).ShouldBeTrue();
        ReferenceEquals(clone.Nodes[0], clone.Next).ShouldBeTrue();
        ReferenceEquals(clone.Nodes[1], clone).ShouldBeTrue();
        ReferenceEquals(clone.Next.Nodes, clone.Nodes).ShouldBeTrue();
        ReferenceEquals(clone.Map.Keys.Single(), clone.Next).ShouldBeTrue();
        ReferenceEquals(clone.Map.Values.Single(), clone.Next).ShouldBeTrue();
        ReferenceEquals(clone.Nodes, root.Nodes).ShouldBeFalse();
        ReferenceEquals(clone.Map, root.Map).ShouldBeFalse();
        ReferenceEquals(clone.Next.Map, clone.Map).ShouldBeTrue();
        clone.Next.Value = 9;
        child.Value.ShouldBe(7);
    }

    [Test]
    public void EditSessionClonesItsInputGraphBeforeEditing()
    {
        var root = new CloneGraphNode();
        var child = new CloneGraphNode { Value = 7, Next = root };
        root.Next = child;
        root.Peer = child;
        root.Nodes.Add(child);
        root.Nodes.Add(root);
        child.Nodes = root.Nodes;

        using var session = new EditSession<CloneGraphNode>(
            root,
            static (_, _) => new ValueTask<StateWriteReceipt>(StateWriteReceipt.Empty)
        );

        ReferenceEquals(session.Value, root).ShouldBeFalse();
        ReferenceEquals(session.Value.Next, session.Value.Peer).ShouldBeTrue();
        ReferenceEquals(session.Value.Next!.Next, session.Value).ShouldBeTrue();
        ReferenceEquals(session.Value.Nodes, session.Value.Next.Nodes).ShouldBeTrue();
        session.Value.Next.Value = 42;
        session.Value.Nodes.Clear();
        child.Value.ShouldBe(7);
        root.Nodes.Count.ShouldBe(2);
        session.SessionStart.Value.Next!.Value.ShouldBe(7);
    }
}
