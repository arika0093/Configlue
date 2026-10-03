using SparseFragments;

namespace SparseFragments.Tests;

[SparseFragmentModel]
public partial class CycleSettings
{
    public int Value { get; set; }
    public CycleSettings? Next { get; set; }
}

[SparseFragmentModel]
public partial class CycleParent
{
    public CycleChild? Child { get; set; }
}

[SparseFragmentModel]
public partial class CycleChild
{
    public int Value { get; set; }
    public CycleParent? Parent { get; set; }
}

[SparseFragmentModel]
public partial class DagRoot
{
    public DagChild? Left { get; set; }
    public DagChild? Right { get; set; }
}

[SparseFragmentModel]
public partial class DagChild
{
    public int Value { get; set; }
}

public sealed class DiffCycleTests
{
    [Test]
    public void Diff_SameReference_ReturnsEmpty()
    {
        var model = new CycleSettings { Value = 1, Next = new CycleSettings { Value = 2 } };

        var diff = CycleSettings.Fragment.Diff(model, model);

        diff.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void Diff_Acyclic_ReturnsMinimalDelta()
    {
        var before = new CycleSettings { Value = 1, Next = new CycleSettings { Value = 1 } };
        var after = new CycleSettings { Value = 1, Next = new CycleSettings { Value = 2 } };

        var diff = CycleSettings.Fragment.Diff(before, after);

        diff.IsEmpty.ShouldBeFalse();
        diff.Value.IsPresent.ShouldBeFalse();
        diff.Next.IsPresent.ShouldBeTrue();
        diff.Next.Value!.Value.IsPresent.ShouldBeTrue();
        diff.Next.Value!.Value.Value.ShouldBe(2);
    }

    [Test]
    public void Diff_SharedDag_DoesNotThrow()
    {
        var sharedBefore = new DagChild { Value = 1 };
        var before = new DagRoot { Left = sharedBefore, Right = sharedBefore };
        var sharedAfter = new DagChild { Value = 1 };
        var after = new DagRoot { Left = sharedAfter, Right = sharedAfter };

        var empty = DagRoot.Fragment.Diff(before, after);

        empty.IsEmpty.ShouldBeTrue();

        var changedAfter = new DagRoot
        {
            Left = new DagChild { Value = 2 },
            Right = new DagChild { Value = 2 },
        };

        var diff = DagRoot.Fragment.Diff(before, changedAfter);

        diff.IsEmpty.ShouldBeFalse();
        diff.Left.IsPresent.ShouldBeTrue();
        diff.Right.IsPresent.ShouldBeTrue();
    }

    [Test]
    public void From_SharedDag_Succeeds()
    {
        var shared = new DagChild { Value = 3 };
        var model = new DagRoot { Left = shared, Right = shared };

        var fragment = DagRoot.Fragment.From(model);

        fragment.Left.IsPresent.ShouldBeTrue();
        fragment.Right.IsPresent.ShouldBeTrue();
        fragment.Left.Value!.Value.Value.ShouldBe(3);
        fragment.Right.Value!.Value.Value.ShouldBe(3);
    }

    [Test]
    public void Diff_SelfCycle_ThrowsNotSupported()
    {
        var original = new CycleSettings { Value = 1 };
        original.Next = original;
        var clone = original.DeepClone();

        ReferenceEquals(clone.Next, clone).ShouldBeTrue();

        var exception = Should.Throw<NotSupportedException>(() =>
            CycleSettings.Fragment.Diff(original, clone)
        );
        exception.Message.ShouldContain("Diff");
        exception.Message.ShouldContain("Next");
    }

    [Test]
    public void From_SelfCycle_ThrowsNotSupported()
    {
        var model = new CycleSettings { Value = 1 };
        model.Next = model;

        var exception = Should.Throw<NotSupportedException>(() =>
            CycleSettings.Fragment.From(model)
        );
        exception.Message.ShouldContain("From");
        exception.Message.ShouldContain("Next");
    }

    [Test]
    public void Diff_TwoNodeCycle_ThrowsNotSupported()
    {
        var a = new CycleSettings { Value = 1 };
        var b = new CycleSettings { Value = 2 };
        a.Next = b;
        b.Next = a;

        var clone = a.DeepClone();

        ReferenceEquals(clone.Next!.Next, clone).ShouldBeTrue();

        var exception = Should.Throw<NotSupportedException>(() =>
            CycleSettings.Fragment.Diff(a, clone)
        );
        exception.Message.ShouldContain("Diff");
        exception.Message.ShouldContain("Next");
    }

    [Test]
    public void From_TwoNodeCycle_ThrowsNotSupported()
    {
        var a = new CycleSettings { Value = 1 };
        var b = new CycleSettings { Value = 2 };
        a.Next = b;
        b.Next = a;

        var exception = Should.Throw<NotSupportedException>(() =>
            CycleSettings.Fragment.From(a)
        );
        exception.Message.ShouldContain("From");
    }

    [Test]
    public void Diff_CrossTypeMutualRefs_ThrowsNotSupported()
    {
        var parent = new CycleParent();
        var child = new CycleChild { Value = 1, Parent = parent };
        parent.Child = child;

        var clone = parent.DeepClone();

        ReferenceEquals(clone.Child!.Parent, clone).ShouldBeTrue();

        var diffException = Should.Throw<NotSupportedException>(() =>
            CycleParent.Fragment.Diff(parent, clone)
        );
        diffException.Message.ShouldContain("Diff");

        var fromException = Should.Throw<NotSupportedException>(() =>
            CycleParent.Fragment.From(parent)
        );
        fromException.Message.ShouldContain("From");
    }

    [Test]
    public void DeepClone_PreservesSelfCycleAndSharedDag()
    {
        var cyclic = new CycleSettings { Value = 5 };
        cyclic.Next = cyclic;
        var cyclicClone = cyclic.DeepClone();

        ReferenceEquals(cyclicClone, cyclic).ShouldBeFalse();
        ReferenceEquals(cyclicClone.Next, cyclicClone).ShouldBeTrue();

        var shared = new DagChild { Value = 7 };
        var root = new DagRoot { Left = shared, Right = shared };
        var rootClone = root.DeepClone();

        ReferenceEquals(rootClone.Left, rootClone.Right).ShouldBeTrue();
        ReferenceEquals(rootClone.Left, shared).ShouldBeFalse();
    }
}
