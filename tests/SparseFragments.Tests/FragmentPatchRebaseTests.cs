using SparseFragments;

namespace SparseFragments.Tests;

public sealed class FragmentPatchRebaseTests
{
    [Test]
    public void ModelRebaseSharesNestedAndCollectionRulesWithoutHostMetadata()
    {
        var baseline = new Settings
        {
            Plugins = ["a"],
            Nested = new Nested { Host = "base", Port = 1 },
        };
        var desired = new Settings
        {
            Plugins = ["a", "b"],
            Nested = new Nested { Host = "base", Port = 2 },
        };
        var current = new Settings
        {
            Plugins = ["a", "c"],
            Nested = new Nested { Host = "upstream", Port = 1 },
        };
        var changes = Settings.Fragment.Diff(baseline, desired);

        var result = SparseModelRebase.Rebase(
            Settings.Fragment.FragmentSchema,
            changes,
            baseline,
            desired,
            current
        );

        result.HasConflicts.ShouldBeFalse();
        var applied = Settings
            .Fragment.From(current)
            .ApplyChanges((Settings.Fragment)result.Patch)
            .ToModel();
        applied.Plugins.ShouldBe(["a", "c", "b"]);
        applied.Nested!.Host.ShouldBe("upstream");
        applied.Nested.Port.ShouldBe(2);
    }

    [Test]
    public void EmptyPatchRebasesAcrossRootPresenceChanges()
    {
        var current = State(new Settings { RetryCount = 9 });
        foreach (
            var baseline in new[]
            {
                Optional<Settings.Fragment?>.Missing,
                Optional<Settings.Fragment?>.Present(null),
            }
        )
        {
            var result = Settings.Patch.Rebase(baseline, new Settings.Patch(), current);
            result.HasConflicts.ShouldBeFalse();
            result.Patch.IsEmpty.ShouldBeTrue();
        }
    }

    [Test]
    public void WholeContributionRebasePreservesFollowingMemberOperations()
    {
        var baseline = State(new Settings { RetryCount = 1 });
        var local = new Settings.Patch();
        local.Set(new Settings { RetryCount = 5 });
        local.RetryCount = 8;
        local.Nested.Port = 7000;

        var result = Settings.Patch.Rebase(baseline, local, baseline);

        result.HasConflicts.ShouldBeFalse();
        var applied = Apply(result.Patch, baseline).Value!.ToModel();
        applied.RetryCount.ShouldBe(8);
        applied.Nested!.Port.ShouldBe(7000);
    }

    [Test]
    public void AppendRebaseDoesNotReplayAnAlreadyAppliedAddition()
    {
        var baseline = FragmentState(
            new Settings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["a"]) }
        );
        var current = FragmentState(
            new Settings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["a", "b"]) }
        );
        var local = new Settings.Patch
        {
            Plugins = new List<string> { "a", "b" },
        };

        var result = Settings.Patch.Rebase(baseline, local, current);

        result.HasConflicts.ShouldBeFalse();
        Apply(result.Patch, current).Value!.Plugins.Value.ShouldBe(["a", "b"]);
    }

    [Test]
    public void NonConflictingScalarEditsMergeOntoCurrent()
    {
        var baseState = State(new Settings { RetryCount = 1, Label = "a" });
        var local = new Settings.Patch { RetryCount = 2 };
        var currentState = State(new Settings { RetryCount = 1, Label = "b" });

        var result = Settings.Patch.Rebase(baseState, local, currentState);

        result.HasConflicts.ShouldBeFalse();
        var model = Apply(result.Patch, currentState).Value!.ToModel();
        model.RetryCount.ShouldBe(2);
        model.Label.ShouldBe("b");
    }

    [Test]
    public void AlreadyAppliedEditBecomesNoOp()
    {
        var baseState = State(new Settings { RetryCount = 1 });
        var local = new Settings.Patch { RetryCount = 2 };
        var currentState = State(new Settings { RetryCount = 2 });

        var result = Settings.Patch.Rebase(baseState, local, currentState);

        result.HasConflicts.ShouldBeFalse();
        result.Patch.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void ScalarConflictReportsStructuredValues()
    {
        var baseState = State(new Settings { RetryCount = 1 });
        var local = new Settings.Patch { RetryCount = 2 };
        var currentState = State(new Settings { RetryCount = 3 });

        var result = Settings.Patch.Rebase(baseState, local, currentState);

        result.HasConflicts.ShouldBeTrue();
        var conflict = result.Conflicts.Single();
        conflict.Kind.ShouldBe(SparsePatchConflictKind.Scalar);
        conflict.Path.ShouldBe(["RetryCount"]);
        conflict.BaseValue.Value.ShouldBe(1);
        conflict.LocalValue.Value.ShouldBe(2);
        conflict.CurrentValue.Value.ShouldBe(3);
    }

    [Test]
    public void NestedConflictExposesNestedMemberPath()
    {
        var baseState = State(new Settings { Nested = new Nested { Host = "a" } });
        var local = new Settings.Patch();
        local.Nested.Host = "b";
        var currentState = State(new Settings { Nested = new Nested { Host = "c" } });

        var result = Settings.Patch.Rebase(baseState, local, currentState);

        result.HasConflicts.ShouldBeTrue();
        var conflict = result.Conflicts.Single();
        conflict.Path.ShouldBe(["Nested", "Host"]);
        conflict.BaseValue.Value.ShouldBe("a");
        conflict.LocalValue.Value.ShouldBe("b");
        conflict.CurrentValue.Value.ShouldBe("c");
    }

    [Test]
    public void NestedNonConflictRebasesOntoCurrent()
    {
        var baseState = State(
            new Settings
            {
                Nested = new Nested { Host = "a", Port = 1 },
            }
        );
        var local = new Settings.Patch();
        local.Nested.Port = 2;
        var currentState = State(
            new Settings
            {
                Nested = new Nested { Host = "changed", Port = 1 },
            }
        );

        var result = Settings.Patch.Rebase(baseState, local, currentState);

        result.HasConflicts.ShouldBeFalse();
        var model = Apply(result.Patch, currentState).Value!.ToModel();
        model.Nested!.Host.ShouldBe("changed");
        model.Nested.Port.ShouldBe(2);
    }

    [Test]
    public void AppendRebaseReplaysAdditionsAfterConcurrentSuffixChange()
    {
        var baseState = FragmentState(
            new Settings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["a"]) }
        );
        var local = new Settings.Patch
        {
            Plugins = new List<string> { "a", "b" },
        };
        var currentState = FragmentState(
            new Settings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["a", "c"]) }
        );

        var result = Settings.Patch.Rebase(baseState, local, currentState);

        result.HasConflicts.ShouldBeFalse();
        Apply(result.Patch, currentState).Value!.Plugins.Value.ShouldBe(["a", "c", "b"]);
    }

    [Test]
    public void AppendRebaseReportsConflictWhenPrefixChanged()
    {
        var baseState = FragmentState(
            new Settings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["a"]) }
        );
        var local = new Settings.Patch
        {
            Plugins = new List<string> { "a", "b" },
        };
        var currentState = FragmentState(
            new Settings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["x"]) }
        );

        var result = Settings.Patch.Rebase(baseState, local, currentState);

        result.HasConflicts.ShouldBeTrue();
        result.Conflicts.Single().Kind.ShouldBe(SparsePatchConflictKind.CollectionAppend);
        result.Conflicts.Single().Path.ShouldBe(["Plugins"]);
    }

    [Test]
    public void SetUnionRebaseAddsNewElementsBesideCurrent()
    {
        var baseState = FragmentState(
            new SetSettings.Fragment
            {
                Values = Optional<ISet<string>>.Present(new HashSet<string> { "a" }),
            }
        );
        var local = new SetSettings.Patch
        {
            Values = new HashSet<string> { "a", "b" },
        };
        var currentState = FragmentState(
            new SetSettings.Fragment
            {
                Values = Optional<ISet<string>>.Present(new HashSet<string> { "a", "c" }),
            }
        );

        var result = SetSettings.Patch.Rebase(baseState, local, currentState);

        result.HasConflicts.ShouldBeFalse();
        var values = Apply(result.Patch, currentState).Value!.Values.Value!;
        values.ShouldContain("a");
        values.ShouldContain("b");
        values.ShouldContain("c");
    }

    [Test]
    public void CustomStrategyParticipatesInRebase()
    {
        var baseState = FragmentState(
            new TraceSettings.Fragment { Tags = Optional<List<string>>.Present(["a"]) }
        );
        var local = new TraceSettings.Patch
        {
            Tags = new List<string> { "a", "b" },
        };
        var currentState = FragmentState(
            new TraceSettings.Fragment { Tags = Optional<List<string>>.Present(["a", "c"]) }
        );

        var result = TraceSettings.Patch.Rebase(baseState, local, currentState);

        result.HasConflicts.ShouldBeFalse();
        Apply(result.Patch, currentState).Value!.Tags.Value.ShouldBe(["a", "c", "b"]);
    }

    [Test]
    public void WholeContributionRebaseConflictsOnConcurrentChange()
    {
        var baseState = State(new Settings { RetryCount = 1 });
        var local = new Settings.Patch();
        local.Set(new Settings { RetryCount = 5 });
        var currentState = State(new Settings { RetryCount = 9 });

        var result = Settings.Patch.Rebase(baseState, local, currentState);

        result.HasConflicts.ShouldBeTrue();
        result.Conflicts.Single().Kind.ShouldBe(SparsePatchConflictKind.WholeContribution);
        result.Conflicts.Single().Path.ShouldBeEmpty();
    }

    [Test]
    public void WholeContributionRebaseAppliesWhenCurrentMatchesBase()
    {
        var baseState = State(new Settings { RetryCount = 1 });
        var local = new Settings.Patch();
        local.Set(new Settings { RetryCount = 5 });
        var currentState = State(new Settings { RetryCount = 1 });

        var result = Settings.Patch.Rebase(baseState, local, currentState);

        result.HasConflicts.ShouldBeFalse();
        Apply(result.Patch, currentState).Value!.RetryCount.Value.ShouldBe(5);
    }

    private static Optional<Settings.Fragment?> State(Settings model) =>
        Optional<Settings.Fragment?>.Present(Settings.Fragment.From(model));

    private static Optional<TFragment?> FragmentState<TFragment>(TFragment fragment)
        where TFragment : class, ISparseFragment => Optional<TFragment?>.Present(fragment);

    private static Optional<Settings.Fragment?> Apply(
        Settings.Patch patch,
        Optional<Settings.Fragment?> state
    ) => ((ISparseModelPatch<Settings, Settings.Fragment>)patch).Apply(state);

    private static Optional<SetSettings.Fragment?> Apply(
        SetSettings.Patch patch,
        Optional<SetSettings.Fragment?> state
    ) => ((ISparseModelPatch<SetSettings, SetSettings.Fragment>)patch).Apply(state);

    private static Optional<TraceSettings.Fragment?> Apply(
        TraceSettings.Patch patch,
        Optional<TraceSettings.Fragment?> state
    ) => ((ISparseModelPatch<TraceSettings, TraceSettings.Fragment>)patch).Apply(state);
}
