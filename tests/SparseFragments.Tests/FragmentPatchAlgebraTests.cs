using SparseFragments;

namespace SparseFragments.Tests;

public sealed class FragmentPatchAlgebraTests
{
    [Test]
    public void AlgebraLawsHoldAcrossSparseRootAndNestedOperations()
    {
        var states = new[]
        {
            Optional<Settings.Fragment?>.Missing,
            Optional<Settings.Fragment?>.Present(null),
            Optional<Settings.Fragment?>.Present(new Settings.Fragment()),
            Optional<Settings.Fragment?>.Present(
                new Settings.Fragment
                {
                    Label = Optional<string?>.Present("initial"),
                    RetryCount = Optional<int>.Present(7),
                    Nested = Optional<Nested.Fragment?>.Present(
                        new Nested.Fragment { Host = Optional<string>.Present("initial") }
                    ),
                }
            ),
        };
        var wholeNull = new Settings.Patch();
        wholeNull.SetNull();
        var wholeUnset = new Settings.Patch();
        wholeUnset.Unset();
        var wholeWithEdits = new Settings.Patch();
        wholeWithEdits.Set(new Settings { RetryCount = 5 });
        wholeWithEdits.RetryCount = 8;
        wholeWithEdits.Nested.Host = "replacement";
        var nestedEdit = new Settings.Patch();
        nestedEdit.Nested.Port = 42;
        var nestedUnset = new Settings.Patch();
        nestedUnset.Nested.Unset();
        var nestedNull = new Settings.Patch();
        nestedNull.Nested.SetNull();
        var nestedReplacement = new Settings.Patch();
        nestedReplacement.Nested.Set(new Nested { Host = "replacement", Port = 12 });
        var memberUnset = new Settings.Patch();
        memberUnset.Label = FragmentOperation<string?>.Unset;
        var patches = new[]
        {
            new Settings.Patch(),
            new Settings.Patch { RetryCount = 0, Label = (string?)null },
            memberUnset,
            wholeNull,
            wholeUnset,
            wholeWithEdits,
            nestedEdit,
            nestedUnset,
            nestedNull,
            nestedReplacement,
        };

        foreach (var state in states)
        foreach (var first in patches)
        {
            AssertSame(state, Apply(first.Invert(state), Apply(first, state)));
            AssertSame(Apply(first, state), Apply(first.Compose(new Settings.Patch()), state));
            AssertSame(Apply(first, state), Apply(new Settings.Patch().Compose(first), state));
            foreach (var second in patches)
            {
                AssertSame(Apply(second, Apply(first, state)), Apply(first.Compose(second), state));
                foreach (var third in patches)
                    AssertSame(
                        Apply(first.Compose(second).Compose(third), state),
                        Apply(first.Compose(second.Compose(third)), state)
                    );
            }
        }
    }

    [Test]
    public void BetweenPreservesMissingNullAndValueStates()
    {
        var before = Optional<Settings.Fragment?>.Present(
            new Settings.Fragment
            {
                Enabled = Optional<bool>.Present(true),
                Label = Optional<string?>.Present("keep"),
            }
        );
        var after = Optional<Settings.Fragment?>.Present(
            new Settings.Fragment
            {
                Enabled = Optional<bool>.Present(false),
                Label = Optional<string?>.Present(null),
                RetryCount = Optional<int>.Present(9),
            }
        );

        var patch = Settings.Patch.Between(before, after);

        AssertSame(after, Apply(patch, before));
        AssertSame(before, Apply(patch.Invert(before), Apply(patch, before)));

        var inverted = patch.Invert(before);
        inverted.RetryCount.Kind.ShouldBe(FragmentOperationKind.Unset);
        inverted.Label.Value.ShouldBe("keep");
    }

    [Test]
    public void BetweenRootPresenceTransitionsRoundTrip()
    {
        var missing = Optional<Settings.Fragment?>.Missing;
        var presentNull = Optional<Settings.Fragment?>.Present(null);
        var presentValue = Optional<Settings.Fragment?>.Present(
            new Settings.Fragment { RetryCount = Optional<int>.Present(4) }
        );

        var transitions = new (
            Optional<Settings.Fragment?> Before,
            Optional<Settings.Fragment?> After
        )[]
        {
            (missing, presentNull),
            (missing, presentValue),
            (presentNull, presentValue),
            (presentValue, presentNull),
            (presentValue, missing),
        };

        foreach (var (before, after) in transitions)
        {
            var patch = Settings.Patch.Between(before, after);
            var applied = Apply(patch, before);
            AssertSame(after, applied);
            AssertSame(before, Apply(patch.Invert(before), applied));
        }
    }

    [Test]
    public void ComposeIdentityAndAssociativityMatchSequentialApply()
    {
        var state = Optional<Settings.Fragment?>.Present(
            Settings.Fragment.From(new Settings { Enabled = true, Label = "start" })
        );
        var first = new Settings.Patch { RetryCount = 12, Label = (string?)null };
        var second = new Settings.Patch { Enabled = false };
        var third = new Settings.Patch();
        third.Nested.Port = 7000;

        AssertSame(Apply(first, state), Apply(first.Compose(new Settings.Patch()), state));
        AssertSame(Apply(first, state), Apply(new Settings.Patch().Compose(first), state));

        var leftAssociated = first.Compose(second).Compose(third);
        var rightAssociated = first.Compose(second.Compose(third));
        AssertSame(Apply(leftAssociated, state), Apply(rightAssociated, state));

        var sequential = Apply(second, Apply(first, state));
        AssertSame(sequential, Apply(first.Compose(second), state));
    }

    [Test]
    public void WholeContributionOperationsComposeWithMemberOperations()
    {
        var state = Optional<Settings.Fragment?>.Present(
            Settings.Fragment.From(new Settings { RetryCount = 1 })
        );
        var whole = new Settings.Patch();
        whole.Set(new Settings { RetryCount = 5, Enabled = false });
        var memberAfterWhole = new Settings.Patch { RetryCount = 7 };
        var memberBeforeWhole = new Settings.Patch { Enabled = true };

        AssertSame(
            Apply(memberAfterWhole, Apply(whole, state)),
            Apply(whole.Compose(memberAfterWhole), state)
        );
        AssertSame(
            Apply(whole, Apply(memberBeforeWhole, state)),
            Apply(memberBeforeWhole.Compose(whole), state)
        );
    }

    [Test]
    public void NestedPatchCompositionAndInversionRoundTrip()
    {
        var before = Optional<Settings.Fragment?>.Present(
            new Settings.Fragment
            {
                Nested = Optional<Nested.Fragment?>.Present(
                    new Nested.Fragment { Host = Optional<string>.Present("host") }
                ),
            }
        );
        var after = Optional<Settings.Fragment?>.Present(
            new Settings.Fragment
            {
                Nested = Optional<Nested.Fragment?>.Present(
                    new Nested.Fragment { Port = Optional<int>.Present(6000) }
                ),
            }
        );

        var patch = Settings.Patch.Between(before, after);
        var rebound = Apply(patch, before);
        AssertSame(after, rebound);
        AssertSame(before, Apply(patch.Invert(before), rebound));

        var noop = Settings.Patch.Between(rebound, rebound);
        noop.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void BetweenOfEqualStatesIsEmpty()
    {
        var state = Optional<Settings.Fragment?>.Present(
            Settings.Fragment.From(new Settings { RetryCount = 4 })
        );

        Settings.Patch.Between(state, state).IsEmpty.ShouldBeTrue();
        Settings
            .Patch.Between(
                Optional<Settings.Fragment?>.Missing,
                Optional<Settings.Fragment?>.Missing
            )
            .IsEmpty.ShouldBeTrue();
    }

    private static Optional<Settings.Fragment?> Apply(
        Settings.Patch patch,
        Optional<Settings.Fragment?> state
    ) => ((ISparseModelPatch<Settings, Settings.Fragment>)patch).Apply(state);

    private static void AssertSame(
        Optional<Settings.Fragment?> expected,
        Optional<Settings.Fragment?> actual
    )
    {
        expected.IsPresent.ShouldBe(actual.IsPresent);
        if (!expected.IsPresent)
        {
            return;
        }

        (expected.Value is null).ShouldBe(actual.Value is null);
        if (expected.Value is null)
        {
            return;
        }

        SameFragment(expected.Value, actual.Value!).ShouldBeTrue();
    }

    private static bool SameFragment(ISparseFragment left, ISparseFragment right)
    {
        var leftMembers = left.EnumeratePresentMembers().ToDictionary(member => member.Id);
        var rightMembers = right.EnumeratePresentMembers().ToDictionary(member => member.Id);
        if (leftMembers.Count != rightMembers.Count)
        {
            return false;
        }

        foreach (var member in leftMembers.Values)
        {
            if (!rightMembers.TryGetValue(member.Id, out var other))
            {
                return false;
            }

            if (member.Value is ISparseFragment leftFragment)
            {
                if (other.Value is not ISparseFragment rightFragment)
                {
                    return false;
                }

                if (!SameFragment(leftFragment, rightFragment))
                {
                    return false;
                }

                continue;
            }

            if (!SparseValueComparer.AreEqual(member.Value, other.Value))
            {
                return false;
            }
        }

        return true;
    }
}
