using Configlue;

namespace Configlue.Tests;

public sealed class GeneratedFragmentMergeTests
{
    [Test]
    public void Merge_WithEmptyHigherPriority_ReturnsLowerOperand()
    {
        var lower = new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
            Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
        };

        var merged = lower.Merge(AppSettings.Fragment.Empty);

        ReferenceEquals(merged, lower).ShouldBeTrue();
        merged.RetryCount.ShouldBe(Optional<int>.Present(3));
        merged.Plugins.Value.ShouldBe(new[] { "base" });
    }

    [Test]
    public void Merge_WithEmptyLowerPriority_ReturnsHigherOperand()
    {
        var higher = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(false),
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
            ),
        };

        var merged = AppSettings.Fragment.Empty.Merge(higher);

        ReferenceEquals(merged, higher).ShouldBeTrue();
        merged.Enabled.ShouldBe(Optional<bool>.Present(false));
        merged.Database.Value!.Port.ShouldBe(Optional<int>.Present(6432));
    }

    [Test]
    public void Merge_WithFullyPresentReplaceFragment_ReturnsHigherOperand()
    {
        var lower = new DatabaseSettings.Fragment { Host = Optional<string>.Present("old") };
        var higher = new DatabaseSettings.Fragment
        {
            Host = Optional<string>.Present("db.local"),
            Port = Optional<int>.Present(6432),
        };

        var merged = lower.Merge(higher);

        ReferenceEquals(merged, higher).ShouldBeTrue();
        merged.Host.ShouldBe(Optional<string>.Present("db.local"));
        merged.Port.ShouldBe(Optional<int>.Present(6432));
    }

    [Test]
    public void Merge_WithCollectionAppend_DoesNotShortCircuitPartialHigherPriority()
    {
        var lower = new AppSettings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
        };
        var higher = new AppSettings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["custom"]),
        };

        var merged = lower.Merge(higher);

        ReferenceEquals(merged, lower).ShouldBeFalse();
        ReferenceEquals(merged, higher).ShouldBeFalse();
        merged.Plugins.Value.ShouldBe(new[] { "base", "custom" });
    }
}
