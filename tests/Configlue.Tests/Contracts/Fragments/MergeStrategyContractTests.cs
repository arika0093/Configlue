namespace Configlue.Tests;

public sealed class MergeStrategyContractTests
{
    [Test]
    public void IConfiglueMergeStrategy_ExposesTypedMergeRebaseAndContributionOperations()
    {
        IConfiglueMergeStrategy strategy = new StringSetMergeStrategy();
        var lower = Optional<object?>.Present((IReadOnlyList<string>)["base", "shared"]);
        var higher = Optional<object?>.Present((IReadOnlyList<string>)["user", "shared"]);

        strategy.ValueType.ShouldBe(typeof(IReadOnlyList<string>));
        var merged = strategy.Merge(lower, higher);
        merged.IsPresent.ShouldBeTrue();
        ((IReadOnlyList<string>)merged.Value!).ShouldBe(["base", "shared", "user"]);
        strategy.AreEqual(new[] { "base", "user" }, new[] { "base", "user" }).ShouldBeTrue();

        ((IConfiglueMergeRebaseStrategy)strategy)
            .TryRebaseObject(
                new[] { "base" },
                new[] { "base", "edit" },
                new[] { "base", "concurrent" },
                out var rebased,
                out var rebaseReason
            )
            .ShouldBeTrue();
        ((IReadOnlyList<string>)rebased!).ShouldBe(["base", "concurrent", "edit"]);
        rebaseReason.ShouldBeNull();

        ConfiglueMergeSourceValue[] sources =
        [
            new(SourceId.From("defaults"), Optional<object?>.Present((IReadOnlyList<string>)["base", "shared"])),
            new(SourceId.From("user"), Optional<object?>.Present((IReadOnlyList<string>)["user"])),
        ];
        ((IConfiglueMergeContributionPlanner)strategy)
            .TryPlanSourceContributionObject(
                sources,
                SourceId.From("user"),
                (IReadOnlyList<string>)["base", "shared", "user", "new"],
                out var contribution,
                out var planReason
            )
            .ShouldBeTrue();
        ((IReadOnlyList<string>)contribution.Value!).ShouldBe(["user", "new"]);
        planReason.ShouldBeNull();
    }

    [Test]
    public void IConfiglueMergeStrategy_ExplainsSourceProvenanceForEachEffectiveElement()
    {
        IConfiglueMergeElementProvenanceProvider strategy = new StringSetMergeStrategy();
        ConfiglueMergeSourceValue[] sources =
        [
            new(SourceId.From("defaults"), Optional<object?>.Present((IReadOnlyList<string>)["base", "shared"])),
            new(SourceId.From("user"), Optional<object?>.Present((IReadOnlyList<string>)["shared", "custom"])),
        ];

        var provenance = strategy.ExplainElementsObject(
            (IReadOnlyList<string>)["base", "shared", "custom"],
            sources
        );

        provenance.Select(static item => item.Index).ShouldBe([0, 1, 2]);
        provenance
            .Select(static item => item.SourceIds.ToArray())
            .ShouldBe([
                [SourceId.From("defaults")],
                [SourceId.From("defaults"), SourceId.From("user")],
                [SourceId.From("user")],
            ]);
    }

    [Test]
    public void MergeOnlyStrategyDoesNotAdvertiseOptionalCapabilities()
    {
        IConfiglueMergeStrategy strategy = new MergeOnlyStrategy();
        strategy.Merge(Optional<object?>.Missing, Optional<object?>.Present("value"))
            .Value.ShouldBe("value");
        ((object)strategy is IConfiglueMergeRebaseStrategy).ShouldBeFalse();
        ((object)strategy is IConfiglueMergeContributionPlanner).ShouldBeFalse();
        ((object)strategy is IConfiglueMergeElementProvenanceProvider).ShouldBeFalse();

        var rebaseOnly = new RebaseOnlyStrategy();
        ((object)rebaseOnly is IConfiglueMergeRebaseStrategy).ShouldBeTrue();
        ((object)rebaseOnly is IConfiglueMergeContributionPlanner).ShouldBeFalse();
        ((object)rebaseOnly is IConfiglueMergeElementProvenanceProvider).ShouldBeFalse();

        var plannerOnly = new PlannerOnlyStrategy();
        ((object)plannerOnly is IConfiglueMergeRebaseStrategy).ShouldBeFalse();
        ((object)plannerOnly is IConfiglueMergeContributionPlanner).ShouldBeTrue();
        ((object)plannerOnly is IConfiglueMergeElementProvenanceProvider).ShouldBeFalse();

        var provenanceOnly = new ProvenanceOnlyStrategy();
        ((object)provenanceOnly is IConfiglueMergeRebaseStrategy).ShouldBeFalse();
        ((object)provenanceOnly is IConfiglueMergeContributionPlanner).ShouldBeFalse();
        ((object)provenanceOnly is IConfiglueMergeElementProvenanceProvider).ShouldBeTrue();

        typeof(IConfiglueMergeStrategy).GetMethods()
            .Select(static method => method.Name)
            .ShouldBe(["get_ValueType", "Merge", "AreEqual"]);
    }

    private class MergeOnlyStrategy : ConfiglueMergeStrategy<string>
    {
        public override Optional<string> Merge(Optional<string> lowerPriority, Optional<string> higherPriority) =>
            higherPriority.IsPresent ? higherPriority : lowerPriority;

        public override bool AreEqual(string? left, string? right) => left == right;
    }

    private sealed class RebaseOnlyStrategy : MergeOnlyStrategy, IConfiglueMergeRebaseStrategy
    {
        public override bool TryRebase(
            string? editBase,
            string? desired,
            string? current,
            out string? rebased,
            out string? reason
        )
        {
            rebased = desired;
            reason = null;
            return true;
        }
    }

    private sealed class PlannerOnlyStrategy : MergeOnlyStrategy, IConfiglueMergeContributionPlanner
    {
        public override bool TryPlanSourceContribution(
            IReadOnlyList<ConfiglueMergeSourceValue<string>> sourceValuesLowToHigh,
            SourceId targetSourceId,
            string? desiredEffective,
            out Optional<string> targetContribution,
            out string? reason
        )
        {
            targetContribution = Optional<string>.Present(desiredEffective);
            reason = null;
            return true;
        }
    }

    private sealed class ProvenanceOnlyStrategy
        : MergeOnlyStrategy,
            IConfiglueMergeElementProvenanceProvider
    {
        public override IReadOnlyList<ConfiglueMergeElementProvenance> ExplainElements(
            string? effective,
            IReadOnlyList<ConfiglueMergeSourceValue<string>> sourceValuesLowToHigh
        ) => [];
    }

    private sealed class StringSetMergeStrategy
        : ConfiglueMergeStrategy<IReadOnlyList<string>>,
            IConfiglueMergeRebaseStrategy,
            IConfiglueMergeContributionPlanner,
            IConfiglueMergeElementProvenanceProvider
    {
        public override Optional<IReadOnlyList<string>> Merge(
            Optional<IReadOnlyList<string>> lowerPriority,
            Optional<IReadOnlyList<string>> higherPriority
        )
        {
            if (!lowerPriority.IsPresent)
            {
                return higherPriority;
            }

            if (!higherPriority.IsPresent)
            {
                return lowerPriority;
            }

            return Optional<IReadOnlyList<string>>.Present(
                Union(lowerPriority.Value!, higherPriority.Value!)
            );
        }

        public override bool AreEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
            left is null ? right is null : right is not null && left.SequenceEqual(right);

        public override bool TryRebase(
            IReadOnlyList<string>? editBase,
            IReadOnlyList<string>? desired,
            IReadOnlyList<string>? current,
            out IReadOnlyList<string>? rebased,
            out string? reason
        )
        {
            if (editBase is null || desired is null || current is null)
            {
                rebased = null;
                reason = "A set merge requires non-null collections.";
                return false;
            }

            rebased = Union(current, desired.Except(editBase, StringComparer.Ordinal));
            reason = null;
            return true;
        }

        public override bool TryPlanSourceContribution(
            IReadOnlyList<ConfiglueMergeSourceValue<IReadOnlyList<string>>> sourceValuesLowToHigh,
            SourceId targetSourceId,
            IReadOnlyList<string>? desiredEffective,
            out Optional<IReadOnlyList<string>> targetContribution,
            out string? reason
        )
        {
            if (desiredEffective is null)
            {
                targetContribution = Optional<IReadOnlyList<string>>.Missing;
                reason = "A set contribution requires a non-null desired value.";
                return false;
            }

            if (!sourceValuesLowToHigh.Any(source => source.SourceId == targetSourceId))
            {
                targetContribution = Optional<IReadOnlyList<string>>.Missing;
                reason = "The target source is not present.";
                return false;
            }

            var otherContributions = sourceValuesLowToHigh
                .Where(source => source.SourceId != targetSourceId && source.Value.IsPresent)
                .SelectMany(static source => source.Value.Value!);
            targetContribution = Optional<IReadOnlyList<string>>.Present(
                desiredEffective.Except(otherContributions, StringComparer.Ordinal).ToArray()
            );
            reason = null;
            return true;
        }

        public override IReadOnlyList<ConfiglueMergeElementProvenance> ExplainElements(
            IReadOnlyList<string>? effective,
            IReadOnlyList<ConfiglueMergeSourceValue<IReadOnlyList<string>>> sourceValuesLowToHigh
        )
        {
            if (effective is null)
            {
                return [];
            }

            return effective
                .Select(
                    (element, index) =>
                        new ConfiglueMergeElementProvenance(
                            index,
                            sourceValuesLowToHigh
                                .Where(source =>
                                    source.Value.IsPresent
                                    && source.Value.Value!.Contains(element, StringComparer.Ordinal)
                                )
                                .Select(static source => source.SourceId)
                        )
                )
                .ToArray();
        }

        private static IReadOnlyList<string> Union(
            IEnumerable<string> first,
            IEnumerable<string> second
        ) => first.Concat(second).Distinct(StringComparer.Ordinal).ToArray();
    }
}
