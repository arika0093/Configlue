using SparseFragments;

namespace SparseFragments.Tests;

[SparseFragmentModel]
public partial class InterfaceDictionarySettings
{
    public IDictionary<string, int> Mutable { get; set; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> ReadOnly { get; set; } = new Dictionary<string, int>();
}

public sealed class InterfaceDictionaryCloneTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void SortedDictionaryInterfaceClonePreservesOrderingComparerAndAliases(bool sortedList)
    {
        IDictionary<string, int> dictionary = sortedList
            ? new SortedList<string, int>(StringComparer.OrdinalIgnoreCase)
            : new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        dictionary.Add("Z", 1);
        dictionary.Add("a", 2);
        var model = new InterfaceDictionarySettings
        {
            Mutable = dictionary,
            ReadOnly = (IReadOnlyDictionary<string, int>)dictionary,
        };
        var clone = model.DeepClone();
        clone.Mutable.GetType().ShouldBe(dictionary.GetType());
        ReferenceEquals(clone.Mutable, clone.ReadOnly).ShouldBeTrue();
        clone.Mutable.Keys.First().ShouldBe("a");
        clone.Mutable.ContainsKey("A").ShouldBeTrue();
        clone.Mutable["A"] = 9;
        dictionary["a"].ShouldBe(2);
    }

    [Test]
    public void RuntimeDictionaryComparerSurvivesInterfaceTypedCloneAndRoundTrip()
    {
        var dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Key"] = 1,
        };
        var model = new InterfaceDictionarySettings { Mutable = dictionary, ReadOnly = dictionary };
        var clone = model.DeepClone();
        ReferenceEquals(clone.Mutable, clone.ReadOnly).ShouldBeTrue();
        clone.Mutable.ContainsKey("KEY").ShouldBeTrue();
        clone.ReadOnly.ContainsKey("KEY").ShouldBeTrue();
        ReferenceEquals(((Dictionary<string, int>)clone.Mutable).Comparer, dictionary.Comparer)
            .ShouldBeTrue();
        clone.Mutable["KEY"] = 2;
        dictionary["key"].ShouldBe(1);
        var restored = InterfaceDictionarySettings.Fragment.From(model).ToModel();
        restored.Mutable.ContainsKey("KEY").ShouldBeTrue();
        restored.ReadOnly.ContainsKey("KEY").ShouldBeTrue();
    }
}
