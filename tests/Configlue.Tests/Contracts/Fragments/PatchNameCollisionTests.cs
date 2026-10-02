using Configlue;

namespace Configlue.Tests;

[ConfiglueModel("patch-name-collision")]
public partial class PatchNameCollisionSettings
{
    public int Set { get; set; } = 1;
    public int SetNull { get; set; } = 2;
    public int Unset { get; set; } = 3;
}

[ConfiglueModel("patch-name-collision-parent")]
public partial class PatchNameCollisionParent
{
    public PatchNameCollisionSettings? Child { get; set; } = new();
}

public sealed class PatchNameCollisionTests
{
    [Test]
    public void OperationNamesRemainUsableAsTypedMemberNames()
    {
        var patch = new PatchNameCollisionSettings.Patch
        {
            Set = FragmentOperation<int>.Set(7),
            SetNull = FragmentOperation<int>.Unset,
            Unset = FragmentOperation<int>.Set(8),
        };
        var current = PatchNameCollisionSettings.Fragment.From(new());
        var changed = (PatchNameCollisionSettings.Fragment)patch.Apply(current);
        changed.Set.Value.ShouldBe(7);
        changed.SetNull.IsPresent.ShouldBeFalse();
        changed.Unset.Value.ShouldBe(8);
    }

    [Test]
    public void WholeModelOperationsRemainAvailableThroughTypedInterface()
    {
        var patch = new PatchNameCollisionParent.Patch();
        IConfiglueModelPatch<PatchNameCollisionSettings> whole = patch.Child;
        whole.Set(new() { Set = 9 });
        var current = PatchNameCollisionParent.Fragment.From(new());
        ((PatchNameCollisionParent.Fragment)patch.Apply(current)).Child.Value!.Set.Value.ShouldBe(
            9
        );
        whole.SetNull();
        var nulled = (PatchNameCollisionParent.Fragment)patch.Apply(current);
        nulled.Child.IsPresent.ShouldBeTrue();
        nulled.Child.Value.ShouldBeNull();
        whole.Unset();
        ((PatchNameCollisionParent.Fragment)patch.Apply(current)).Child.IsPresent.ShouldBeFalse();
        var replacement = new PatchNameCollisionParent.Patch().WithUnspecifiedMembersUnset();
        (
            (PatchNameCollisionParent.Fragment)replacement.Apply(current)
        ).Child.IsPresent.ShouldBeFalse();
    }
}
