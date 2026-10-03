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

        strategy
            .TryRebase(
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
        strategy
            .TryPlanSourceContribution(
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
        IConfiglueMergeStrategy strategy = new StringSetMergeStrategy();
        ConfiglueMergeSourceValue[] sources =
        [
            new(SourceId.From("defaults"), Optional<object?>.Present((IReadOnlyList<string>)["base", "shared"])),
            new(SourceId.From("user"), Optional<object?>.Present((IReadOnlyList<string>)["shared", "custom"])),
        ];

        var provenance = strategy.ExplainElements(
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

    private sealed class StringSetMergeStrategy : ConfiglueMergeStrategy<IReadOnlyList<string>>
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
