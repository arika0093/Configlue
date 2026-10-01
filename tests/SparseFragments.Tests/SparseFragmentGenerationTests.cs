using SparseFragments;

namespace SparseFragments.Tests;

[SparseFragmentModel]
public partial class Settings
{
    public bool Enabled { get; set; } = true;

    public int RetryCount { get; set; } = 3;

    public string? Label { get; set; } = "default";

    public Nested? Nested { get; set; } = new();

    [SparseMerge(MergeMode.Append)]
    public IReadOnlyList<string> Plugins { get; set; } = [];
}

[SparseFragmentModel]
public partial class Nested
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;
}

public sealed class SumMergeStrategy : FragmentMergeStrategy<List<int>>
{
    public override Optional<List<int>> Merge(
        Optional<List<int>> lowerPriority,
        Optional<List<int>> higherPriority
    )
    {
        if (!higherPriority.IsPresent)
        {
            return lowerPriority;
        }

        if (!lowerPriority.IsPresent)
        {
            return higherPriority;
        }

        return Optional<List<int>>.Present(
            lowerPriority.Value!.Zip(higherPriority.Value!, (a, b) => a + b).ToList()
        );
    }

    public override bool AreEqual(List<int>? left, List<int>? right) =>
        (left is null && right is null)
        || (left is not null && right is not null && left.SequenceEqual(right));
}

[SparseFragmentModel]
public partial class StrategySettings
{
    [SparseMerge(typeof(SumMergeStrategy))]
    public List<int> Values { get; set; } = [];
}

public sealed class SparseFragmentGenerationTests
{
    [Test]
    public void MissingPresentNullAndDefaultRemainDistinct()
    {
        var fragment = new Settings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Label = Optional<string?>.Present(null),
        };

        var value = fragment.ToModel();

        fragment.Enabled.IsPresent.ShouldBeTrue();
        fragment.Enabled.Value.ShouldBeFalse();
        fragment.RetryCount.IsPresent.ShouldBeFalse();
        fragment.Label.IsPresent.ShouldBeTrue();
        fragment.Label.Value.ShouldBeNull();
        value.Enabled.ShouldBeFalse();
        value.RetryCount.ShouldBe(3);
        value.Nested!.Host.ShouldBe("localhost");
    }

    [Test]
    public void FromModelCopiesMutableCollections()
    {
        var plugins = new List<string> { "before-save" };
        var model = new Settings { Plugins = plugins };

        var fragment = Settings.Fragment.From(model);
        plugins.Add("after-save");

        fragment.Plugins.Value.ShouldBe(["before-save"]);
    }

    [Test]
    public void MergeDeepMergesNestedAndAppendsCollections()
    {
        var lower = new Settings.Fragment
        {
            Nested = Optional<Nested.Fragment?>.Present(
                new Nested.Fragment { Host = Optional<string>.Present("db.local") }
            ),
            Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
        };
        var higher = new Settings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Nested = Optional<Nested.Fragment?>.Present(
                new Nested.Fragment { Port = Optional<int>.Present(6432) }
            ),
            Plugins = Optional<IReadOnlyList<string>>.Present(["custom"]),
        };

        var merged = lower.Merge(higher).ToModel();

        merged.Enabled.ShouldBeFalse();
        merged.Nested!.Host.ShouldBe("db.local");
        merged.Nested.Port.ShouldBe(6432);
        merged.Plugins.ShouldBe(["base", "custom"]);
    }

    [Test]
    public void DiffAndApplyChangesDistinguishSetFromUnset()
    {
        var before = new Settings
        {
            Enabled = true,
            RetryCount = 3,
            Label = "old",
        };
        var after = new Settings
        {
            Enabled = false,
            RetryCount = 3,
            Label = null,
        };

        var diff = Settings.Fragment.Diff(before, after);
        var applied = Settings.Fragment.From(before).ApplyChanges(diff);

        diff.Enabled.Value.ShouldBeFalse();
        diff.RetryCount.IsPresent.ShouldBeFalse();
        diff.Label.IsPresent.ShouldBeTrue();
        diff.Label.Value.ShouldBeNull();
        applied.Enabled.Value.ShouldBeFalse();
        applied.Label.Value.ShouldBeNull();
    }

    [Test]
    public void DeepCloneIsIndependent()
    {
        var original = new Settings
        {
            Nested = new Nested { Host = "clone-me" },
            Plugins = ["a"],
        };

        var clone = original.DeepClone();
        clone.Nested!.Host = "mutated";
        clone.Plugins = ["b"];

        original.Nested!.Host.ShouldBe("clone-me");
        original.Plugins.ShouldBe(["a"]);
    }

    [Test]
    public void FragmentCloneIsIndependent()
    {
        var fragment = new Settings.Fragment
        {
            Nested = Optional<Nested.Fragment?>.Present(
                new Nested.Fragment { Host = Optional<string>.Present("clone-me") }
            ),
        };

        var clone = fragment.DeepClone();

        clone.ShouldNotBeSameAs(fragment);
        clone.Nested.Value.ShouldNotBeSameAs(fragment.Nested.Value);
        clone.Nested.Value!.Host.Value.ShouldBe("clone-me");
    }

    [Test]
    public void CustomMergeStrategyIsApplied()
    {
        var lower = new StrategySettings.Fragment
        {
            Values = Optional<List<int>>.Present([1, 2]),
        };
        var higher = new StrategySettings.Fragment
        {
            Values = Optional<List<int>>.Present([10, 20]),
        };

        var merged = lower.Merge(higher).ToModel();

        merged.Values.ShouldBe([11, 22]);
    }

    [Test]
    public void DescriptorEnumeratesAndMutatesMembers()
    {
        var fragment = new Settings.Fragment { RetryCount = Optional<int>.Present(9) };
        var members = fragment.EnumeratePresentMembers().ToArray();

        members.Length.ShouldBe(1);
        members[0].Name.ShouldBe("RetryCount");
        members[0].Value.ShouldBe(9);

        var added = (Settings.Fragment)fragment.WithMember(3, new[] { "x" });
        added.Plugins.Value.ShouldBe(["x"]);
        var removed = (Settings.Fragment)fragment.WithoutMember(4);
        removed.RetryCount.IsPresent.ShouldBeFalse();
    }
}
