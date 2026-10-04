namespace SparseFragments.Tests;

public sealed class FragmentEqualityTests
{
    [Test]
    public void EmptyFragmentsAreEqual()
    {
        SparseFragmentComparer.AreEqual(new Settings.Fragment(), new Settings.Fragment()).ShouldBeTrue();
    }

    [Test]
    public void SameInstanceIsEqual()
    {
        var fragment = new Settings.Fragment { Enabled = Optional<bool>.Present(false) };
        SparseFragmentComparer.AreEqual(fragment, fragment).ShouldBeTrue();
    }

    [Test]
    public void NullHandling()
    {
        SparseFragmentComparer.AreEqual(null, null).ShouldBeTrue();
        SparseFragmentComparer
            .AreEqual(new Settings.Fragment(), null)
            .ShouldBeFalse();
        SparseFragmentComparer
            .AreEqual(null, new Settings.Fragment())
            .ShouldBeFalse();
    }

    [Test]
    public void MissingPresentNullAndPresentValueRemainDistinct()
    {
        var missing = new Settings.Fragment();
        var presentNull = new Settings.Fragment { Label = Optional<string?>.Present(null) };
        var presentValue = new Settings.Fragment { Label = Optional<string?>.Present("x") };
        var otherNull = new Settings.Fragment { Label = Optional<string?>.Present(null) };

        SparseFragmentComparer.AreEqual(missing, presentNull).ShouldBeFalse();
        SparseFragmentComparer.AreEqual(presentNull, presentValue).ShouldBeFalse();
        SparseFragmentComparer.AreEqual(presentNull, otherNull).ShouldBeTrue();
    }

    [Test]
    public void DifferentPresentCountsAreUnequal()
    {
        var one = new Settings.Fragment { Enabled = Optional<bool>.Present(false) };
        var two = new Settings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            RetryCount = Optional<int>.Present(3),
        };

        SparseFragmentComparer.AreEqual(one, two).ShouldBeFalse();
        SparseFragmentComparer.AreEqual(two, one).ShouldBeFalse();
    }

    [Test]
    public void SameCountDifferentMembersAreUnequal()
    {
        var left = new Settings.Fragment { Enabled = Optional<bool>.Present(false) };
        var right = new Settings.Fragment { RetryCount = Optional<int>.Present(3) };

        SparseFragmentComparer.AreEqual(left, right).ShouldBeFalse();
    }

    [Test]
    public void DifferentFragmentTypesAreUnequal()
    {
        var settings = new Settings.Fragment { RetryCount = Optional<int>.Present(1) };
        var nested = new Nested.Fragment { Port = Optional<int>.Present(1) };

        SparseFragmentComparer.AreEqual(settings, nested).ShouldBeFalse();
    }

    [Test]
    public void NestedFragmentsCompareStructurally()
    {
        var left = new Settings.Fragment
        {
            Nested = Optional<Nested.Fragment?>.Present(
                new Nested.Fragment { Host = Optional<string>.Present("db.local") }
            ),
        };
        var right = new Settings.Fragment
        {
            Nested = Optional<Nested.Fragment?>.Present(
                new Nested.Fragment { Host = Optional<string>.Present("db.local") }
            ),
        };
        var changed = new Settings.Fragment
        {
            Nested = Optional<Nested.Fragment?>.Present(
                new Nested.Fragment
                {
                    Host = Optional<string>.Present("db.local"),
                    Port = Optional<int>.Present(6432),
                }
            ),
        };
        var missing = new Settings.Fragment();

        SparseFragmentComparer.AreEqual(left, right).ShouldBeTrue();
        SparseFragmentComparer.AreEqual(left, changed).ShouldBeFalse();
        SparseFragmentComparer.AreEqual(left, missing).ShouldBeFalse();
    }

    [Test]
    public void NestedFragmentVersusNullIsUnequal()
    {
        var fragment = new Settings.Fragment
        {
            Nested = Optional<Nested.Fragment?>.Present(
                new Nested.Fragment { Host = Optional<string>.Present("db.local") }
            ),
        };
        var presentNull = new Settings.Fragment
        {
            Nested = Optional<Nested.Fragment?>.Present(null),
        };

        SparseFragmentComparer.AreEqual(fragment, presentNull).ShouldBeFalse();
        SparseFragmentComparer
            .AreEqual(presentNull, new Settings.Fragment { Nested = Optional<Nested.Fragment?>.Present(null) })
            .ShouldBeTrue();
    }

    [Test]
    public void SequencesAreOrderSensitive()
    {
        var left = new Settings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["a", "b"]),
        };
        var same = new Settings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["a", "b"]),
        };
        var reordered = new Settings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["b", "a"]),
        };
        var shorter = new Settings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["a"]) };

        SparseFragmentComparer.AreEqual(left, same).ShouldBeTrue();
        SparseFragmentComparer.AreEqual(left, reordered).ShouldBeFalse();
        SparseFragmentComparer.AreEqual(left, shorter).ShouldBeFalse();
    }

    [Test]
    public void ArraysAndListsWithEqualContentAreEqual()
    {
        SparseValueComparer.AreEqual(new[] { 1, 2, 3 }, new List<int> { 1, 2, 3 }).ShouldBeTrue();
        SparseValueComparer.AreEqual(new[] { 1, 2, 3 }, new List<int> { 3, 2, 1 }).ShouldBeFalse();
        SparseValueComparer.AreEqual(new[] { 1, 2 }, new[] { 1, 2, 3 }).ShouldBeFalse();
    }

    [Test]
    public void DictionariesAreOrderIndependent()
    {
        var left = new DictionarySettings.Fragment
        {
            Values = Optional<Dictionary<string, int>>.Present(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["first"] = 1,
                    ["second"] = 2,
                }
            ),
        };
        var reordered = new DictionarySettings.Fragment
        {
            Values = Optional<Dictionary<string, int>>.Present(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["second"] = 2,
                    ["first"] = 1,
                }
            ),
        };
        var changed = new DictionarySettings.Fragment
        {
            Values = Optional<Dictionary<string, int>>.Present(
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["first"] = 1,
                    ["second"] = 3,
                }
            ),
        };

        SparseFragmentComparer.AreEqual(left, reordered).ShouldBeTrue();
        SparseFragmentComparer.AreEqual(left, changed).ShouldBeFalse();
    }

    [Test]
    public void DictionariesUseNativeKeyLookup()
    {
        var left = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["FIRST"] = 1 };
        var right = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["first"] = 1 };
        var different = new Dictionary<string, int> { ["first"] = 1, ["second"] = 2 };

        SparseValueComparer.AreEqual(left, right).ShouldBeTrue();
        SparseValueComparer.AreEqual(left, different).ShouldBeFalse();
        SparseValueComparer.AreEqual(left, new List<int> { 1 }).ShouldBeFalse();
    }

    [Test]
    public void SetsAreOrderIndependentAndUseNativeSemantics()
    {
        var left = new SetSettings.Fragment
        {
            Values = Optional<ISet<string>>.Present(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alpha", "beta" }
            ),
        };
        var reordered = new SetSettings.Fragment
        {
            Values = Optional<ISet<string>>.Present(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BETA", "ALPHA" }
            ),
        };
        var changed = new SetSettings.Fragment
        {
            Values = Optional<ISet<string>>.Present(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alpha", "gamma" }
            ),
        };

        SparseFragmentComparer.AreEqual(left, reordered).ShouldBeTrue();
        SparseFragmentComparer.AreEqual(left, changed).ShouldBeFalse();
    }

    [Test]
    public void SetComparedAgainstSequenceIsUnequal()
    {
        SparseValueComparer
            .AreEqual(new HashSet<string> { "a" }, new List<string> { "a" })
            .ShouldBeFalse();
    }

    [Test]
    public void NestedFragmentsInsideCollectionsCompareStructurally()
    {
        var leftNested = new Nested.Fragment { Host = Optional<string>.Present("db.local") };
        var rightNested = new Nested.Fragment { Host = Optional<string>.Present("db.local") };
        var changedNested = new Nested.Fragment { Host = Optional<string>.Present("other") };

        SparseValueComparer
            .AreEqual(new List<object?> { leftNested }, new List<object?> { rightNested })
            .ShouldBeTrue();
        SparseValueComparer
            .AreEqual(new List<object?> { leftNested }, new List<object?> { changedNested })
            .ShouldBeFalse();
        SparseValueComparer
            .AreEqual(
                new Dictionary<string, object?> { ["nested"] = leftNested },
                new Dictionary<string, object?> { ["nested"] = rightNested }
            )
            .ShouldBeTrue();
        SparseValueComparer
            .AreEqual(leftNested, new Dictionary<string, object?>())
            .ShouldBeFalse();
        SparseValueComparer.AreEqual(new Dictionary<string, object?>(), leftNested).ShouldBeFalse();
    }

    [Test]
    public void ScalarsAndNulls()
    {
        SparseValueComparer.AreEqual(null, null).ShouldBeTrue();
        SparseValueComparer.AreEqual(null, 1).ShouldBeFalse();
        SparseValueComparer.AreEqual(1, null).ShouldBeFalse();
        SparseValueComparer.AreEqual(1, 1).ShouldBeTrue();
        SparseValueComparer.AreEqual(1, 2).ShouldBeFalse();
        SparseValueComparer.AreEqual("a", "a").ShouldBeTrue();
        SparseValueComparer.AreEqual("a", "b").ShouldBeFalse();
    }

    [Test]
    public void OptionalWrapperPreservesPresence()
    {
        var fragment = new Settings.Fragment { Enabled = Optional<bool>.Present(false) };

        SparseFragmentComparer
            .AreEqual(Optional<Settings.Fragment?>.Present(fragment), Optional<Settings.Fragment?>.Present(fragment))
            .ShouldBeTrue();
        SparseFragmentComparer
            .AreEqual(Optional<Settings.Fragment?>.Present(fragment), Optional<Settings.Fragment?>.Missing)
            .ShouldBeFalse();
        SparseFragmentComparer
            .AreEqual(Optional<Settings.Fragment?>.Missing, Optional<Settings.Fragment?>.Missing)
            .ShouldBeTrue();
    }
}
