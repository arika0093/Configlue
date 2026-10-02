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
    public void RuntimeDictionaryComparerSurvivesInterfaceTypedCloneAndRoundTrip()
    {
        var dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Key"] = 1,
        };
        var model = new InterfaceDictionarySettings { Mutable = dictionary, ReadOnly = dictionary };
        var clone = model.DeepClone();
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
