using SparseFragments;

namespace SparseFragments.Tests;

[SparseFragmentModel]
public partial class PrimaryConstructionSettings(int count = 7, int[]? items = null)
{
    public int Count { get; } = count;
    public int[]? Items { get; } = items;
}

[SparseFragmentModel]
public partial record PositionalConstructionSettings(int Count = 7, int[]? Items = null);

public sealed class PrimaryConstructionTests
{
    [Test]
    public void PrimaryConstructorRestoresGetterOnlyPropertiesAndClonesCollections()
    {
        PrimaryConstructionSettings.Fragment.Empty.ToModel().Count.ShouldBe(7);
        var source = new[] { 1, 2 };
        var model = new PrimaryConstructionSettings.Fragment
        {
            Count = Optional<int>.Present(0),
            Items = Optional<int[]?>.Present(source),
        }.ToModel();
        model.Count.ShouldBe(0);
        var clone = model.DeepClone();
        clone.Count.ShouldBe(0);
        ReferenceEquals(clone.Items, model.Items).ShouldBeFalse();
        clone.Items![0] = 9;
        model.Items![0].ShouldBe(1);
        PrimaryConstructionSettings.Fragment.From(model).ToModel().Items![1].ShouldBe(2);
    }

    [Test]
    public void PositionalRecordUsesDeclaredDefaultsAndRoundTrips()
    {
        PositionalConstructionSettings.Fragment.Empty.ToModel().Count.ShouldBe(7);
        var model = new PositionalConstructionSettings(0, new[] { 1, 2 });
        var restored = PositionalConstructionSettings.Fragment.From(model).ToModel();
        restored.Count.ShouldBe(0);
        restored.Items![1].ShouldBe(2);
        ReferenceEquals(restored.Items, model.Items).ShouldBeFalse();
        var clone = model.DeepClone();
        clone.Count.ShouldBe(0);
        ReferenceEquals(clone.Items, model.Items).ShouldBeFalse();
    }
}

[SparseFragmentModel]
public partial class ImmutableChildSettings
{
    public ImmutableConstructionChild Child { get; set; } = new(7);
    public List<ImmutableConstructionChild> Children { get; set; } = new();
}

public sealed class ImmutableConstructionChild(int count = 7, int[]? items = null)
{
    private ImmutableConstructionChild()
        : this(99, null) { }

    public int Count { get; } = count;
    public int[]? Items { get; } = items;
}

public sealed class ImmutableChildConstructionTests
{
    [Test]
    public void StructuralGetterOnlyChildRoundTripsAndClonesInsideCollections()
    {
        var child = new ImmutableConstructionChild(3, new[] { 1, 2 });
        var model = new ImmutableChildSettings
        {
            Child = child,
            Children = new() { child },
        };
        var updated = new ImmutableChildSettings
        {
            Child = new ImmutableConstructionChild(4, child.Items),
            Children = model.Children,
        };
        var patch = ImmutableChildSettings.Fragment.Diff(model, updated);
        patch.Child.Value!.Items.IsPresent.ShouldBeFalse();
        var merged = patch.ToModel(model);
        merged.Child.Count.ShouldBe(4);
        merged.Child.Items![1].ShouldBe(2);
        merged.Child.Items[0] = 8;
        child.Items![0].ShouldBe(1);
        var restored = ImmutableChildSettings.Fragment.From(model).ToModel();
        restored.Child.Count.ShouldBe(3);
        restored.Child.Items![1].ShouldBe(2);
        ReferenceEquals(restored.Child, child).ShouldBeFalse();
        var clone = model.DeepClone();
        ReferenceEquals(clone.Children[0], child).ShouldBeFalse();
        ReferenceEquals(clone.Children[0].Items, child.Items).ShouldBeFalse();
        clone.Children[0].Items![0] = 9;
        child.Items![0].ShouldBe(1);
        ImmutableChildSettings.Fragment.Empty.ToModel().Child.Count.ShouldBe(7);
    }
}

[SparseFragmentModel]
public partial class InitChildSettings
{
    public InitConstructionChild Child { get; set; } = new();
    public List<InitConstructionChild> Children { get; set; } = new();
}

public sealed class InitConstructionChild(int count = 7, int[]? items = null)
{
    public int Count { get; init; } = count;
    public int[]? Items { get; init; } = items;
}

public sealed class InitChildConstructionTests
{
    [Test]
    public void ConstructorBoundInitChildRoundTripsAndClonesCollections()
    {
        var child = new InitConstructionChild(3, new[] { 1, 2 });
        var model = new InitChildSettings
        {
            Child = child,
            Children = new() { child },
        };
        var updated = new InitChildSettings
        {
            Child = new InitConstructionChild(4, child.Items),
            Children = model.Children,
        };
        var patch = InitChildSettings.Fragment.Diff(model, updated);
        patch.Child.Value!.Items.IsPresent.ShouldBeFalse();
        var merged = patch.ToModel(model);
        merged.Child.Count.ShouldBe(4);
        merged.Child.Items![1].ShouldBe(2);
        merged.Child.Items[0] = 8;
        child.Items![0].ShouldBe(1);
        var restored = InitChildSettings.Fragment.From(model).ToModel();
        restored.Child.Count.ShouldBe(3);
        restored.Child.Items![1].ShouldBe(2);
        ReferenceEquals(restored.Child, child).ShouldBeFalse();
        var clone = model.DeepClone();
        ReferenceEquals(clone.Children[0].Items, child.Items).ShouldBeFalse();
        clone.Children[0].Items![0] = 9;
        child.Items![0].ShouldBe(1);
        InitChildSettings.Fragment.Empty.ToModel().Child.Count.ShouldBe(7);
    }
}

[SparseFragmentModel]
public partial class RequiredChildSettings
{
    public RequiredConstructionChild Child { get; set; } = new();
    public List<RequiredConstructionChild> Children { get; set; } = new();
}

public sealed class RequiredConstructionChild
{
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public RequiredConstructionChild(int count = 7, int[]? items = null)
    {
        Count = count;
        Items = items;
    }

    public required int Count { get; init; }
    public required int[]? Items { get; init; }
}

public sealed class RequiredChildConstructionTests
{
    [Test]
    public void RequiredChildUsesAnnotatedConstructorForProjectionAndClone()
    {
        var child = new RequiredConstructionChild(3, new[] { 1, 2 });
        var model = new RequiredChildSettings
        {
            Child = child,
            Children = new() { child },
        };
        var updated = new RequiredChildSettings
        {
            Child = new RequiredConstructionChild(4, child.Items),
            Children = model.Children,
        };
        var patch = RequiredChildSettings.Fragment.Diff(model, updated);
        patch.Child.Value!.Items.IsPresent.ShouldBeFalse();
        var merged = patch.ToModel(model);
        merged.Child.Count.ShouldBe(4);
        merged.Child.Items![1].ShouldBe(2);
        merged.Child.Items[0] = 8;
        child.Items![0].ShouldBe(1);
        var restored = RequiredChildSettings.Fragment.From(model).ToModel();
        restored.Child.Count.ShouldBe(3);
        restored.Child.Items![1].ShouldBe(2);
        var clone = model.DeepClone();
        clone.Children[0].Count.ShouldBe(3);
        ReferenceEquals(clone.Children[0].Items, child.Items).ShouldBeFalse();
        clone.Children[0].Items![0] = 9;
        child.Items![0].ShouldBe(1);
        RequiredChildSettings.Fragment.Empty.ToModel().Child.Count.ShouldBe(7);
    }
}

[SparseFragmentModel]
public partial class BaselineConstructionSettings
{
    public BaselineConstructionSettings(int count, string name = "base", List<int>? items = null)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        Count = count;
        Name = name;
        Items = items ?? new();
    }

    public int Count { get; }
    public string Name { get; set; }

    [SparseMerge(MergeMode.Append)]
    public List<int> Items { get; set; }
}

public sealed class BaselineConstructionTests
{
    [Test]
    public void BaselineProjectionRetainsRequiredArgumentsAndUsesMergeStrategies()
    {
        var baseline = new BaselineConstructionSettings(3, "old", new() { 1 });
        var patch = new BaselineConstructionSettings.Fragment
        {
            Name = Optional<string>.Present("new"),
            Items = Optional<List<int>>.Present(new() { 2 }),
        };
        var projected = patch.ToModel(baseline);
        projected.Count.ShouldBe(3);
        projected.Name.ShouldBe("new");
        projected.Items.ShouldBe(new[] { 1, 2 });
        projected.Items[0] = 9;
        baseline.Items.ShouldBe(new[] { 1 });
        baseline.Name.ShouldBe("old");
        BaselineConstructionSettings.Fragment.Empty.ToModel(baseline).Count.ShouldBe(3);
    }
}
