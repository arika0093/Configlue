using Configlue;

namespace Example.MultiSource;

[ConfiglueModel("example.multi-source-settings", Version = 1)]
public partial class SampleSetting
{
    public string Name { get; set; } = "Global default";

    public string Policy { get; set; } = "Global policy";

    public string Region { get; set; } = "Global region";

    [ConfiglueMerge(typeof(FeatureMergeStrategy))]
    public IReadOnlyList<string> Features { get; set; } = [];

    public sealed class FeatureMergeStrategy : ConfiglueMergeStrategy<IReadOnlyList<string>>
    {
        public override Optional<IReadOnlyList<string>> Merge(
            Optional<IReadOnlyList<string>> lowerPriority,
            Optional<IReadOnlyList<string>> higherPriority
        )
        {
            if (!higherPriority.IsPresent)
            {
                return lowerPriority;
            }

            if (
                !lowerPriority.IsPresent
                || lowerPriority.Value is null
                || higherPriority.Value is null
            )
            {
                return higherPriority;
            }

            return Optional<IReadOnlyList<string>>.Present(
                lowerPriority
                    .Value.Concat(higherPriority.Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            );
        }

        public override bool AreEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
            ReferenceEquals(left, right)
            || (
                left is not null
                && right is not null
                && left.SequenceEqual(right, StringComparer.Ordinal)
            );

        public override bool TryRebase(
            IReadOnlyList<string>? editBase,
            IReadOnlyList<string>? desired,
            IReadOnlyList<string>? current,
            out IReadOnlyList<string>? rebased,
            out string? reason
        )
        {
            if (AreEqual(editBase, current) || AreEqual(desired, current))
            {
                rebased = desired;
                reason = null;
                return true;
            }

            rebased = current;
            reason = "Concurrent feature edits cannot be combined by this strategy.";
            return false;
        }

        public override bool TryPlanSourceContribution(
            IReadOnlyList<ConfiglueMergeSourceValue<IReadOnlyList<string>>> sourceValuesLowToHigh,
            string targetSourceId,
            IReadOnlyList<string>? desiredEffective,
            out Optional<IReadOnlyList<string>> targetContribution,
            out string? reason
        )
        {
            var targetIndex = -1;
            var otherValues = new HashSet<string>(StringComparer.Ordinal);
            for (var sourceIndex = 0; sourceIndex < sourceValuesLowToHigh.Count; sourceIndex++)
            {
                var source = sourceValuesLowToHigh[sourceIndex];
                if (string.Equals(source.SourceId, targetSourceId, StringComparison.Ordinal))
                {
                    targetIndex = sourceIndex;
                    continue;
                }

                if (source.Value.IsPresent && source.Value.Value is { } contribution)
                {
                    otherValues.UnionWith(contribution);
                }
            }

            if (targetIndex < 0 || desiredEffective is null)
            {
                targetContribution = Optional<IReadOnlyList<string>>.Missing;
                reason = "The target source is missing or the desired feature list is null.";
                return false;
            }

            var targetValues = desiredEffective
                .Where(value => !otherValues.Contains(value))
                .ToArray();
            var resolved = new List<string>();
            for (var sourceIndex = 0; sourceIndex < sourceValuesLowToHigh.Count; sourceIndex++)
            {
                IEnumerable<string> values;
                if (sourceIndex == targetIndex)
                {
                    values = targetValues;
                }
                else if (
                    sourceValuesLowToHigh[sourceIndex].Value.IsPresent
                    && sourceValuesLowToHigh[sourceIndex].Value.Value is not null
                )
                {
                    values = sourceValuesLowToHigh[sourceIndex].Value.Value!;
                }
                else
                {
                    values = [];
                }

                foreach (var value in values)
                {
                    if (!resolved.Contains(value, StringComparer.Ordinal))
                    {
                        resolved.Add(value);
                    }
                }
            }

            if (!resolved.SequenceEqual(desiredEffective, StringComparer.Ordinal))
            {
                targetContribution = Optional<IReadOnlyList<string>>.Missing;
                reason = "Other source feature contributions prevent the requested ordering.";
                return false;
            }

            targetContribution = Optional<IReadOnlyList<string>>.Present(targetValues);
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
                    (value, index) =>
                        new ConfiglueMergeElementProvenance(
                            index,
                            sourceValuesLowToHigh
                                .Where(source =>
                                    source.Value.IsPresent
                                    && source.Value.Value?.Contains(value, StringComparer.Ordinal)
                                        == true
                                )
                                .Select(static source => source.SourceId)
                        )
                )
                .ToArray();
        }
    }
}
