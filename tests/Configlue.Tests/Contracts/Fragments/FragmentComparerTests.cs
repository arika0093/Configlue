namespace Configlue.Tests;

[ConfiglueModel("fragment-equality-nested")]
public partial class FragmentEqualityNestedSettings
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;
}

[ConfiglueModel("fragment-equality-root")]
public partial class FragmentEqualityRootSettings
{
    public string Name { get; set; } = "root";

    public int Counter { get; set; } = 3;

    public bool Enabled { get; set; } = true;

    public FragmentEqualityNestedSettings? Nested { get; set; } = new();

    public List<string> Tags { get; set; } = [];

    public Dictionary<string, int> Lookup { get; set; } = new();

    public HashSet<string> Flags { get; set; } = [];
}

[ConfiglueModel("fragment-equality-other")]
public partial class FragmentEqualityOtherSettings
{
    public string Name { get; set; } = "other";
}

public sealed class FragmentComparerTests
{
    [Test]
    public void EmptyFragmentsAreEqual()
    {
        ConfiglueFragmentComparer
            .AreEqual(new FragmentEqualityRootSettings.Fragment(), new FragmentEqualityRootSettings.Fragment())
            .ShouldBeTrue();
    }

    [Test]
    public void PresentCountMismatchIsUnequal()
    {
        var one = new FragmentEqualityRootSettings.Fragment
        {
            Name = Optional<string>.Present("root"),
        };
        var two = new FragmentEqualityRootSettings.Fragment
        {
            Name = Optional<string>.Present("root"),
            Counter = Optional<int>.Present(3),
        };

        ConfiglueFragmentComparer.AreEqual(one, two).ShouldBeFalse();
        ConfiglueFragmentComparer.AreEqual(two, one).ShouldBeFalse();
    }

    [Test]
    public void SameCountDifferentMembersAreUnequal()
    {
        var left = new FragmentEqualityRootSettings.Fragment
        {
            Name = Optional<string>.Present("root"),
        };
        var right = new FragmentEqualityRootSettings.Fragment
        {
            Counter = Optional<int>.Present(3),
        };

        ConfiglueFragmentComparer.AreEqual(left, right).ShouldBeFalse();
    }

    [Test]
    public void MissingPresentNullAndPresentValueRemainDistinct()
    {
        var missing = new FragmentEqualityRootSettings.Fragment();
        var presentNull = new FragmentEqualityRootSettings.Fragment
        {
            Nested = Optional<FragmentEqualityNestedSettings.Fragment?>.Present(null),
        };
        var presentValue = new FragmentEqualityRootSettings.Fragment
        {
            Nested = Optional<FragmentEqualityNestedSettings.Fragment?>.Present(
                new FragmentEqualityNestedSettings.Fragment()
            ),
        };

        ConfiglueFragmentComparer.AreEqual(missing, presentNull).ShouldBeFalse();
        ConfiglueFragmentComparer.AreEqual(presentNull, presentValue).ShouldBeFalse();
        ConfiglueFragmentComparer
            .AreEqual(
                presentNull,
                new FragmentEqualityRootSettings.Fragment
                {
                    Nested = Optional<FragmentEqualityNestedSettings.Fragment?>.Present(null),
                }
            )
            .ShouldBeTrue();
    }

    [Test]
    public void NestedFragmentsCompareStructurally()
    {
        var left = NestedFragment(5432);
        var right = NestedFragment(5432);
        var changed = NestedFragment(6432);

        ConfiglueFragmentComparer.AreEqual(left, right).ShouldBeTrue();
        ConfiglueFragmentComparer.AreEqual(left, changed).ShouldBeFalse();
    }

    [Test]
    public void SequencesAreOrderSensitive()
    {
        var left = TagsFragment(["a", "b"]);
        var same = TagsFragment(["a", "b"]);
        var reordered = TagsFragment(["b", "a"]);

        ConfiglueFragmentComparer.AreEqual(left, same).ShouldBeTrue();
        ConfiglueFragmentComparer.AreEqual(left, reordered).ShouldBeFalse();
    }

    [Test]
    public void DictionariesAreOrderIndependent()
    {
        var left = LookupFragment(
            new Dictionary<string, int> { ["first"] = 1, ["second"] = 2 }
        );
        var reordered = LookupFragment(
            new Dictionary<string, int> { ["second"] = 2, ["first"] = 1 }
        );
        var changed = LookupFragment(
            new Dictionary<string, int> { ["first"] = 1, ["second"] = 3 }
        );

        ConfiglueFragmentComparer.AreEqual(left, reordered).ShouldBeTrue();
        ConfiglueFragmentComparer.AreEqual(left, changed).ShouldBeFalse();
    }

    [Test]
    public void DictionariesUseNativeKeyLookup()
    {
        var left = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["FIRST"] = 1 };
        var right = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["first"] = 1 };

        ConfiglueValueComparer.AreEqual(left, right).ShouldBeTrue();
    }

    [Test]
    public void SetsAreOrderIndependentAndUseNativeSemantics()
    {
        var left = FlagsFragment(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alpha", "beta" });
        var reordered = FlagsFragment(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BETA", "ALPHA" }
        );
        var changed = FlagsFragment(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alpha", "gamma" }
        );

        ConfiglueFragmentComparer.AreEqual(left, reordered).ShouldBeTrue();
        ConfiglueFragmentComparer.AreEqual(left, changed).ShouldBeFalse();
    }

    [Test]
    public void NestedFragmentsInsideCollectionsCompareStructurally()
    {
        var leftNested = new FragmentEqualityNestedSettings.Fragment
        {
            Host = Optional<string>.Present("db.local"),
        };
        var rightNested = new FragmentEqualityNestedSettings.Fragment
        {
            Host = Optional<string>.Present("db.local"),
        };

        ConfiglueValueComparer
            .AreEqual(new List<object?> { leftNested }, new List<object?> { rightNested })
            .ShouldBeTrue();
        ConfiglueValueComparer
            .AreEqual(
                new Dictionary<string, object?> { ["nested"] = leftNested },
                new Dictionary<string, object?> { ["nested"] = rightNested }
            )
            .ShouldBeTrue();
    }

    [Test]
    public void SchemaMismatchIsUnequal()
    {
        IConfiglueFragment left = new FragmentEqualityRootSettings.Fragment
        {
            Name = Optional<string>.Present("root"),
        };
        IConfiglueFragment right = new FragmentEqualityOtherSettings.Fragment
        {
            Name = Optional<string>.Present("root"),
        };

        ConfiglueFragmentComparer.AreEqual(left, right).ShouldBeFalse();
    }

    private static FragmentEqualityRootSettings.Fragment NestedFragment(int port) =>
        new()
        {
            Nested = Optional<FragmentEqualityNestedSettings.Fragment?>.Present(
                new FragmentEqualityNestedSettings.Fragment
                {
                    Host = Optional<string>.Present("localhost"),
                    Port = Optional<int>.Present(port),
                }
            ),
        };

    private static FragmentEqualityRootSettings.Fragment TagsFragment(List<string> tags) =>
        new() { Tags = Optional<List<string>>.Present(tags) };

    private static FragmentEqualityRootSettings.Fragment LookupFragment(
        Dictionary<string, int> lookup
    ) => new() { Lookup = Optional<Dictionary<string, int>>.Present(lookup) };

    private static FragmentEqualityRootSettings.Fragment FlagsFragment(HashSet<string> flags) =>
        new() { Flags = Optional<HashSet<string>>.Present(flags) };
}
