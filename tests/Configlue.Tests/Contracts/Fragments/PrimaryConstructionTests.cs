using Configlue;

namespace Configlue.Tests;

[ConfiglueModel("primary-construction")]
public partial class PrimaryConstructionSettings(int count = 7, int[]? items = null)
{
    public int Count { get; } = count;
    public int[]? Items { get; } = items;
}

[ConfiglueModel("positional-construction")]
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

[ConfiglueModel("immutable-child-construction")]
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

[ConfiglueModel("init-child-construction")]
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
