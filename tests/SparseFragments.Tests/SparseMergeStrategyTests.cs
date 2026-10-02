using SparseFragments;

namespace SparseFragments.Tests;

public sealed class DistinctUnionMergeStrategy : FragmentMergeStrategy<List<string>>
{
    public override Optional<List<string>> Merge(
        Optional<List<string>> lowerPriority,
        Optional<List<string>> higherPriority
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

        var merged = new List<string>(lowerPriority.Value!);
        foreach (var value in higherPriority.Value!)
        {
            if (!merged.Contains(value))
            {
                merged.Add(value);
            }
        }

        return Optional<List<string>>.Present(merged);
    }

    public override bool AreEqual(List<string>? left, List<string>? right) =>
        (left is null && right is null)
        || (left is not null && right is not null && left.SequenceEqual(right));

    public override bool TryRebase(
        List<string>? editBase,
        List<string>? desired,
        List<string>? current,
        out List<string>? rebased,
        out string? reason
    )
    {
        var result = current is null ? [] : new List<string>(current);
        foreach (var value in desired ?? [])
        {
            if (!result.Contains(value))
            {
                result.Add(value);
            }
        }

        rebased = result;
        reason = null;
        return true;
    }

    public override IReadOnlyList<SparseMergeElementProvenance> ExplainElements(
        List<string>? effective,
        IReadOnlyList<SparseContribution<List<string>>> contributionsLowToHigh
    )
    {
        if (effective is null)
        {
            return [];
        }

        var result = new List<SparseMergeElementProvenance>();
        for (var index = 0; index < effective.Count; index++)
        {
            var element = effective[index];
            var sources = contributionsLowToHigh
                .Where(contribution =>
                    contribution.Value.IsPresent
                    && contribution.Value.Value is not null
                    && contribution.Value.Value.Contains(element)
                )
                .Select(contribution => contribution.Index);
            result.Add(new SparseMergeElementProvenance(index, sources));
        }

        return result;
    }
}

[SparseFragmentModel]
public partial class TraceSettings
{
    [SparseMerge(typeof(DistinctUnionMergeStrategy))]
    public List<string> Tags { get; set; } = [];
}

[SparseFragmentModel]
public partial class ReplaceTraceSettings
{
    public List<string> Values { get; set; } = [];
}

public sealed class SparseMergeStrategyTests
{
    [Test]
    public void SetUnionTraceUsesTheSourceSetsComparer()
    {
        var lower = new SetSettings.Fragment
        {
            Values = Optional<ISet<string>>.Present(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alpha" }
            ),
        };
        var higher = new SetSettings.Fragment
        {
            Values = Optional<ISet<string>>.Present(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ALPHA" }
            ),
        };
        var trace = SparseMergeTracer
            .Explain(
                SetSettings.Fragment.FragmentSchema,
                lower.Merge(higher),
                [
                    new(0, Optional<object?>.Present(lower)),
                    new(1, Optional<object?>.Present(higher)),
                ]
            )
            .Single();
        trace.Elements.Single().ContributionIndices.ShouldBe([0, 1]);
    }

    [Test]
    public void ReplaceCollectionTraceUsesOnlyTheWinningContribution()
    {
        var lower = new ReplaceTraceSettings.Fragment
        {
            Values = Optional<List<string>>.Present(["same", "lower"]),
        };
        var higher = new ReplaceTraceSettings.Fragment
        {
            Values = Optional<List<string>>.Present(["same", "higher"]),
        };
        var trace = SparseMergeTracer
            .Explain(
                ReplaceTraceSettings.Fragment.FragmentSchema,
                lower.Merge(higher),
                [
                    new(0, Optional<object?>.Present(lower)),
                    new(1, Optional<object?>.Present(higher)),
                ]
            )
            .Single();
        trace.ContributionIndices.ShouldBe([1]);
        trace.Elements.Count.ShouldBe(2);
        foreach (var element in trace.Elements)
            element.ContributionIndices.ShouldBe([1]);
    }

    [Test]
    public void AppendTraceDoesNotAttributeElementsErasedByNullToLowerContributions()
    {
        var lower = new Settings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["same"]),
        };
        var reset = new Settings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(null!),
        };
        var higher = new Settings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["same"]),
        };
        var trace = SparseMergeTracer.Explain(
            Settings.Fragment.FragmentSchema,
            lower.Merge(reset).Merge(higher),
            [
                new(0, Optional<object?>.Present(lower)),
                new(1, Optional<object?>.Present(reset)),
                new(2, Optional<object?>.Present(higher)),
            ]
        );
        var plugins = trace.Single(member => member.Name == "Plugins");
        plugins.ContributionIndices.ShouldBe([1, 2]);
        plugins.Elements.Single().ContributionIndices.ShouldBe([2]);
    }

    [Test]
    public void DefaultRebasePreservesCurrentForAnUnchangedLocalValue()
    {
        var strategy = new SumMergeStrategy();
        strategy.TryRebase([1], [1], [5], out var result, out _).ShouldBeTrue();
        result.ShouldBe([5]);
    }

    [Test]
    public void DeepTraceDropsContributionsErasedByPresentNull()
    {
        var lower = new Settings.Fragment
        {
            Nested = Optional<Nested.Fragment?>.Present(
                new Nested.Fragment { Host = Optional<string>.Present("erased") }
            ),
        };
        var reset = new Settings.Fragment { Nested = Optional<Nested.Fragment?>.Present(null) };
        var higher = new Settings.Fragment
        {
            Nested = Optional<Nested.Fragment?>.Present(
                new Nested.Fragment { Port = Optional<int>.Present(9) }
            ),
        };
        var effective = lower.Merge(reset).Merge(higher);
        var trace = SparseMergeTracer.Explain(
            Settings.Fragment.FragmentSchema,
            effective,
            [
                new(0, Optional<object?>.Present(lower)),
                new(1, Optional<object?>.Present(reset)),
                new(2, Optional<object?>.Present(higher)),
            ]
        );

        var nested = trace.Single(member => member.Name == "Nested");
        nested.ContributionIndices.ShouldBe([1, 2]);
        var host = nested.Nested.Single(member => member.Name == "Host");
        host.Value.IsPresent.ShouldBeFalse();
        host.ContributionIndices.ShouldBeEmpty();
        nested.Nested.Single(member => member.Name == "Port").ContributionIndices.ShouldBe([2]);
    }

    [Test]
    public void DefaultTryRebaseReconcilesSharedValuesAndReportsConflicts()
    {
        var strategy = new SumMergeStrategy();

        strategy.TryRebase([1], [9], [1], out var rebased, out var reason).ShouldBeTrue();
        rebased.ShouldBe([9]);
        reason.ShouldBeNull();

        strategy.TryRebase([1], [9], [9], out var alreadyApplied, out _).ShouldBeTrue();
        alreadyApplied.ShouldBe([9]);

        strategy.TryRebase([1], [9], [5], out _, out var conflictReason).ShouldBeFalse();
        conflictReason.ShouldNotBeNull();
    }

    [Test]
    public void DefaultTryPlanContributionOnlyPlansHighestPriority()
    {
        var strategy = new SumMergeStrategy();
        var contributions = new SparseContribution<List<int>>[]
        {
            new(0, Optional<List<int>>.Present([1])),
            new(1, Optional<List<int>>.Present([2])),
        };

        strategy.TryPlanContribution(contributions, 1, [5], out var planned, out _).ShouldBeTrue();
        planned.Value.ShouldBe([5]);

        strategy.TryPlanContribution(contributions, 0, [5], out _, out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull();
    }

    [Test]
    public void TracerExplainsReplaceAndDeepMembers()
    {
        var lower = new Settings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
            Nested = Optional<Nested.Fragment?>.Present(
                new Nested.Fragment { Host = Optional<string>.Present("lower") }
            ),
        };
        var higher = new Settings.Fragment
        {
            RetryCount = Optional<int>.Present(9),
            Nested = Optional<Nested.Fragment?>.Present(
                new Nested.Fragment { Port = Optional<int>.Present(6432) }
            ),
        };
        var effective = lower.Merge(higher);

        var trace = SparseMergeTracer.Explain(
            Settings.Fragment.FragmentSchema,
            effective,
            [
                new SparseContribution(0, Optional<object?>.Present((object)lower)),
                new SparseContribution(1, Optional<object?>.Present((object)higher)),
            ]
        );

        var retry = trace.Single(member => member.Name == "RetryCount");
        retry.ContributionIndices.ShouldBe([1]);
        retry.Value.Value.ShouldBe(9);

        var nested = trace.Single(member => member.Name == "Nested");
        nested.ContributionIndices.ShouldBe([0, 1]);
        nested.Nested.Single(member => member.Name == "Host").Value.Value.ShouldBe("lower");
        nested.Nested.Single(member => member.Name == "Port").ContributionIndices.ShouldBe([1]);
    }

    [Test]
    public void TracerExplainsAppendElements()
    {
        var lower = new Settings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
        };
        var higher = new Settings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["first", "second"]),
        };
        var effective = lower.Merge(higher);

        var trace = SparseMergeTracer.Explain(
            Settings.Fragment.FragmentSchema,
            effective,
            [
                new SparseContribution(0, Optional<object?>.Present((object)lower)),
                new SparseContribution(1, Optional<object?>.Present((object)higher)),
            ]
        );

        var plugins = trace.Single(member => member.Name == "Plugins");
        plugins.ContributionIndices.ShouldBe([0, 1]);
        plugins.Elements.Select(element => element.Index).ShouldBe([0, 1, 2]);
        plugins.Elements[0].ContributionIndices.ShouldBe([0]);
        plugins.Elements[1].ContributionIndices.ShouldBe([1]);
        plugins.Elements[2].ContributionIndices.ShouldBe([1]);
    }

    [Test]
    public void TracerExplainsSetUnionElements()
    {
        var lower = new SetSettings.Fragment
        {
            Values = Optional<ISet<string>>.Present(new HashSet<string> { "alpha", "beta" }),
        };
        var higher = new SetSettings.Fragment
        {
            Values = Optional<ISet<string>>.Present(new HashSet<string> { "beta", "gamma" }),
        };
        var effective = lower.Merge(higher);

        var trace = SparseMergeTracer.Explain(
            SetSettings.Fragment.FragmentSchema,
            effective,
            [
                new SparseContribution(0, Optional<object?>.Present((object)lower)),
                new SparseContribution(1, Optional<object?>.Present((object)higher)),
            ]
        );

        var values = trace.Single(member => member.Name == "Values");
        values.ContributionIndices.ShouldBe([0, 1]);
        foreach (var element in values.Elements)
        {
            element.ContributionIndices.Count.ShouldBeGreaterThan(0);
        }
    }

    [Test]
    public void TracerDelegatesCustomStrategyElementExplanation()
    {
        var lower = new TraceSettings.Fragment
        {
            Tags = Optional<List<string>>.Present(["alpha", "beta"]),
        };
        var higher = new TraceSettings.Fragment
        {
            Tags = Optional<List<string>>.Present(["beta", "gamma"]),
        };
        var effective = lower.Merge(higher);

        var trace = SparseMergeTracer.Explain(
            TraceSettings.Fragment.FragmentSchema,
            effective,
            [
                new SparseContribution(0, Optional<object?>.Present((object)lower)),
                new SparseContribution(1, Optional<object?>.Present((object)higher)),
            ]
        );

        var tags = trace.Single(member => member.Name == "Tags");
        ((List<string>)tags.Value.Value!).ShouldBe(["alpha", "beta", "gamma"]);
        tags.Elements.Count.ShouldBe(3);
        tags.Elements[0].ContributionIndices.ShouldBe([0]);
        tags.Elements[1].ContributionIndices.ShouldBe([0, 1]);
        tags.Elements[2].ContributionIndices.ShouldBe([1]);
    }
}
