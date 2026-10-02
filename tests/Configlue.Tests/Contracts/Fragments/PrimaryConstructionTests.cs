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
