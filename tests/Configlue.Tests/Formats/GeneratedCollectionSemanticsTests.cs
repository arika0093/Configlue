using Configlue;

namespace Configlue.Tests;

[ConfiglueModel("collection-semantics")]
public partial class CollectionSemanticsSettings
{
    [ConfiglueMerge(MergeMode.SetUnion)]
    public ISet<string> Values { get; set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GeneratedCollectionSemanticsTests
{
    [Test]
    public void DiffUsesSetComparerAndDictionaryKeysRatherThanEnumerationOrder()
    {
        var before = new CollectionSemanticsSettings
        {
            Values = new HashSet<string>(["alpha"], StringComparer.OrdinalIgnoreCase),
            Entries = new(StringComparer.OrdinalIgnoreCase) { ["first"] = 1, ["second"] = 2 },
        };
        var after = new CollectionSemanticsSettings
        {
            Values = new HashSet<string>(["ALPHA"], StringComparer.OrdinalIgnoreCase),
            Entries = new(StringComparer.OrdinalIgnoreCase) { ["SECOND"] = 2, ["FIRST"] = 1 },
        };

        CollectionSemanticsSettings.Fragment.Diff(before, after).IsEmpty.ShouldBeTrue();
        CollectionSemanticsSettings.Fragment.Diff(after, before).IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void SetUnionPreservesTheConcreteLowerComparerThroughProjection()
    {
        var lower = CollectionSemanticsSettings.Fragment.From(
            new CollectionSemanticsSettings
            {
                Values = new HashSet<string>(["alpha"], StringComparer.OrdinalIgnoreCase),
            }
        );
        var higher = new CollectionSemanticsSettings.Fragment
        {
            Values = Optional<ISet<string>>.Present(new HashSet<string> { "ALPHA", "beta" }),
        };

        var projected = lower.Merge(higher).DeepClone().ToModel();

        projected.Values.Count.ShouldBe(2);
        projected.Values.Contains("BETA").ShouldBeTrue();
        ((HashSet<string>)projected.Values).Comparer.ShouldBeSameAs(
            StringComparer.OrdinalIgnoreCase
        );
        lower.Values.Value!.Count.ShouldBe(1);
    }
}
