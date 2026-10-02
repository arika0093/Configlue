using Configlue.CompilerServices;

namespace Configlue.Tests;

public sealed class PatchContractTests
{
    [Test]
    public void IConfigluePatch_ReportsEmptinessAndAppliesOperations()
    {
        IConfigluePatch empty = new AppSettings.Patch();
        IConfigluePatch patch = new AppSettings.Patch
        {
            RetryCount = FragmentOperation<int>.Set(9),
        };
        var current = new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
            Label = Optional<string?>.Present("preserved"),
        };

        empty.Schema.ShouldBe(AppSettings.ConfiglueSchema);
        empty.IsEmpty.ShouldBeTrue();
        patch.IsEmpty.ShouldBeFalse();
        var updated = (AppSettings.Fragment)patch.Apply(current);
        updated.RetryCount.Value.ShouldBe(9);
        updated.Label.Value.ShouldBe("preserved");
    }

    [Test]
    public void IConfiglueDynamicMemberPatch_SelectsOnlyRequestedStableMemberIds()
    {
        IConfiglueDynamicMemberPatch patch = new AppSettings.Patch
        {
            RetryCount = FragmentOperation<int>.Set(5),
            Label = FragmentOperation<string?>.Set("not selected"),
        };
        var retryCountId = AppSettings
            .FragmentSchema.Members.Single(static member => member.Name == "RetryCount")
            .Id;
        var current = new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(3),
            Label = Optional<string?>.Present("preserved"),
        };

        var selected = patch.SelectMembers([retryCountId]);
        var selectedResult = (AppSettings.Fragment)selected.Apply(current);
        var unknown = patch.SelectMembers([int.MaxValue]);

        selected.IsEmpty.ShouldBeFalse();
        selectedResult.RetryCount.Value.ShouldBe(5);
        selectedResult.Label.Value.ShouldBe("preserved");
        unknown.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void IConfiglueReplacementPatch_UnsetsMembersOmittedFromThePatch()
    {
        IConfiglueReplacementPatch patch = new AppSettings.Patch
        {
            RetryCount = FragmentOperation<int>.Set(5),
        };
        var current = new AppSettings.Fragment
        {
            Enabled = Optional<bool>.Present(true),
            RetryCount = Optional<int>.Present(3),
            Label = Optional<string?>.Present("remove"),
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment { Host = Optional<string>.Present("remove.db") }
            ),
        };

        var replacement = patch.WithUnspecifiedMembersUnset();
        var updated = (AppSettings.Fragment)replacement.Apply(current);

        updated.RetryCount.ShouldBe(Optional<int>.Present(5));
        updated.Enabled.IsPresent.ShouldBeFalse();
        updated.Label.IsPresent.ShouldBeFalse();
        updated.Database.IsPresent.ShouldBeFalse();
    }

    [Test]
    public void IConfiglueRoutablePatch_SplitsOperationsByConfiguredSource()
    {
        IConfiglueRoutablePatch patch = new AppSettings.Patch
        {
            RetryCount = FragmentOperation<int>.Set(5),
            Label = FragmentOperation<string?>.Set("local"),
        };
        var plan = new StateWritePlan(
            null,
            new Dictionary<string, SourceId>(StringComparer.Ordinal) { ["RetryCount"] = SourceId.From("settings") }
        );

        var routed = patch.Route(plan, SourceId.From("default"));

        routed.Keys.OrderBy(static key => key).ShouldBe([SourceId.From("default"), SourceId.From("settings")]);
        (
            (AppSettings.Fragment)routed[SourceId.From("settings")].Apply(new AppSettings.Fragment())
        ).RetryCount.Value.ShouldBe(5);
        (
            (AppSettings.Fragment)routed[SourceId.From("settings")].Apply(new AppSettings.Fragment())
        ).Label.IsPresent.ShouldBeFalse();
        var fallback = (AppSettings.Fragment)routed[SourceId.From("default")].Apply(new AppSettings.Fragment());
        fallback.Label.Value.ShouldBe("local");
        fallback.RetryCount.IsPresent.ShouldBeFalse();
    }
}
