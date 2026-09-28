namespace Configlue.Tests;

public sealed class GeneratedPathTests
{
    [Test]
    public void TypedRoutesKeepRepeatedNestedModelsSeparateAndPreferLongestIdentity()
    {
        var plan = StateWritePlan
            .For<RootWithTwoSettings>()
            .Route(x => x.Left, SourceKey<RootWithTwoSettings>.Named("left"))
            .Route(x => x.Left!.Inner!.Count, SourceKey<RootWithTwoSettings>.Named("inner"))
            .Route(x => x.Right!.Label, SourceKey<RootWithTwoSettings>.Named("right"))
            .Build();

        ConfiglueWriteRouting.Resolve(plan, Path("Left.Label"), "fallback").ShouldBe("left");
        ConfiglueWriteRouting.Resolve(plan, Path("Left.Inner.Count"), "fallback").ShouldBe("inner");
        ConfiglueWriteRouting.Resolve(plan, Path("Right.Label"), "fallback").ShouldBe("right");
        ConfiglueWriteRouting
            .Resolve(plan, Path("Right.Inner.Count"), "fallback")
            .ShouldBe("fallback");
        ConfiglueWriteRouting.HasRouteBelow(plan, Path("Left")).ShouldBeTrue();
        ConfiglueWriteRouting.HasRouteBelow(plan, Path("Right.Inner")).ShouldBeFalse();
    }

    [Test]
    public void GeneratedPathsPreserveRootIdentityAndRejectCrossModelPlans()
    {
        var root = ConfiglueMemberPath.FromNames(
            RootWithNestedSettings.ConfiglueSchema,
            "Settings.Label"
        );
        var sibling = Path("Left.Label");
        root.Equals(sibling).ShouldBeFalse();
        var first = StateWritePlan
            .For<RootWithNestedSettings>()
            .Route(x => x.Settings!.Label, SourceKey<RootWithNestedSettings>.Named("first"))
            .Build();
        var second = StateWritePlan
            .For<RootWithTwoSettings>()
            .Route(x => x.Left!.Label, SourceKey<RootWithTwoSettings>.Named("second"))
            .Build();

        Should.Throw<ArgumentException>(() => ConfiglueWriteRouting.Resolve(first, sibling));
        Should.Throw<ArgumentException>(() => first.OverrideWith(second));
    }

    [Test]
    public void OverrideRetainsCompiledRootAndReadableDiagnostics()
    {
        var first = StateWritePlan
            .For<RootWithTwoSettings>()
            .Route(x => x.Left, SourceKey<RootWithTwoSettings>.Named("parent"))
            .Build();
        var second = StateWritePlan
            .For<RootWithTwoSettings>()
            .Route(x => x.Left!.Label, SourceKey<RootWithTwoSettings>.Named("leaf"))
            .Build();
        var merged = first.OverrideWith(second);
        ConfiglueWriteRouting.Resolve(merged, Path("Left.Label")).ShouldBe("leaf");
        ConfiglueWriteRouting.Resolve(merged, Path("Left.Inner.Count")).ShouldBe("parent");
        Path("Left.Inner.Count").ToString().ShouldBe("Left.Inner.Count");
        var invalid = new StateWritePlan(
            new Dictionary<string, string> { ["Left.Missing"] = "source" }
        );
        Should
            .Throw<ArgumentException>(() =>
                ConfiglueWriteRouting.Bind(invalid, RootWithTwoSettings.ConfiglueSchema)
            )
            .Message.ShouldContain("Left.Missing");
    }

    [Test]
    public void GeneratedNestedPatchRoutesByIdentityAndNullConflictNamesThePath()
    {
        var plan = StateWritePlan
            .For<RootWithTwoSettings>()
            .Route(x => x.Left!.Label, SourceKey<RootWithTwoSettings>.Named("left"))
            .Route(x => x.Right!.Label, SourceKey<RootWithTwoSettings>.Named("right"))
            .Build();
        IConfiglueRoutablePatch patch = new RootWithTwoSettings.Patch
        {
            Left = new NestedSettings.Patch { Label = FragmentOperation<string?>.Set("L") },
            Right = new NestedSettings.Patch { Label = FragmentOperation<string?>.Set("R") },
        };
        var routed = patch.Route(plan, "fallback");
        var left = (RootWithTwoSettings.Fragment)
            routed["left"].Apply(new RootWithTwoSettings.Fragment());
        var right = (RootWithTwoSettings.Fragment)
            routed["right"].Apply(new RootWithTwoSettings.Fragment());
        left.Left.Value!.Label.Value.ShouldBe("L");
        left.Right.IsPresent.ShouldBeFalse();
        right.Right.Value!.Label.Value.ShouldBe("R");
        right.Left.IsPresent.ShouldBeFalse();

        var nullChild = new NestedSettings.Patch();
        nullChild.SetNull();
        IConfiglueRoutablePatch nullPatch = new RootWithTwoSettings.Patch { Left = nullChild };
        Should
            .Throw<NotSupportedException>(() => nullPatch.Route(plan, "fallback"))
            .Message.ShouldContain("Left");
    }

    [Test]
    public void IdentityNavigationReadsSparseAndNullNestedValues()
    {
        var fragment = new RootWithTwoSettings.Fragment
        {
            Left = Optional<NestedSettings.Fragment?>.Present(
                new NestedSettings.Fragment { Label = Optional<string?>.Present("L") }
            ),
            Right = Optional<NestedSettings.Fragment?>.Present(null),
        };
        Path("Left.Label").TryGetFragmentValue(fragment, out var label).ShouldBeTrue();
        label.ShouldBe("L");
        Path("Right.Label").TryGetFragmentValue(fragment, out _).ShouldBeFalse();
        Path("Left.Inner.Count").TryGetFragmentValue(fragment, out _).ShouldBeFalse();
        var model = new RootWithTwoSettings { Left = null };
        Path("Left.Label").GetModelValue(model, out var leaf).ShouldBeNull();
        leaf.Name.ShouldBe("Label");
    }

    private static ConfiglueMemberPath Path(string path) =>
        ConfiglueMemberPath.FromNames(RootWithTwoSettings.ConfiglueSchema, path);
}
