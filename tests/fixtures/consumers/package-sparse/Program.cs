using SparseFragments;

var original = Settings.Fragment.From(
    new Settings
    {
        Label = "original",
        Child = new Child { Count = 7, Host = "keep" },
    }
);
var patch = new Settings.Patch { Label = (string?)null };
patch.Child.Count = 9;
var result = original.Apply(patch);
Require(result.Label.IsPresent && result.Label.Value is null, "present null");
Require(result.Child.Value!.Count.Value == 9, "typed nested Set");
Require(result.Child.Value.Host.Value == "keep", "unchanged child member");
Require(original.Child.Value!.Count.Value == 7, "original fragment isolation");

patch = new Settings.Patch();
patch.Child.Unset();
Require(!original.Apply(patch).Child.IsPresent, "nested Unset");
patch = new Settings.Patch();
patch.Child.SetNull();
result = original.Apply(patch);
Require(result.Child.IsPresent && result.Child.Value is null, "nested SetNull");

var builder = original.ToBuilder();
builder.Label = Optional<string?>.Missing;
Require(!builder.Build().Label.IsPresent, "typed builder presence");
var clone = original.ToModel().DeepClone();
clone.Child!.Count = 42;
Require(original.ToModel().Child!.Count == 7, "structural deep clone isolation");

var before = Optional<Settings.Fragment?>.Present(original);
var edits = new Settings.Patch { Label = (string?)null };
edits.Child.Count = 11;
var next = new Settings.Patch();
next.Child.Host = "next";
var combined = edits.Compose(next);
var applied = ((ISparseModelPatch<Settings, Settings.Fragment>)combined).Apply(before);
var restored = ((ISparseModelPatch<Settings, Settings.Fragment>)combined.Invert(before)).Apply(
    applied
);
Require(
    restored.Value!.Label.Value == "original" && restored.Value.Child.Value!.Count.Value == 7,
    "exact inversion"
);
Require(
    applied.Value!.Child.Value!.Count.Value == 11 && applied.Value.Child.Value.Host.Value == "next",
    "patch composition"
);
var between = Settings.Patch.Between(before, applied);
Require(
    ((ISparseModelPatch<Settings, Settings.Fragment>)between)
        .Apply(before)
        .Value!.Child.Value!.Host.Value == "next",
    "sparse Between"
);
var upstream = original.ToBuilder();
upstream.Label = Optional<string?>.Present("upstream");
var local = new Settings.Patch();
local.Child.Count = 12;
var rebased = Settings.Patch.Rebase(
    before,
    local,
    Optional<Settings.Fragment?>.Present(upstream.Build())
);
Require(!rebased.HasConflicts, "structured rebase");
var replayed = ((ISparseModelPatch<Settings, Settings.Fragment>)rebased.Patch).Apply(
    Optional<Settings.Fragment?>.Present(upstream.Build())
);
Require(
    replayed.Value!.Label.Value == "upstream" && replayed.Value.Child.Value!.Count.Value == 12,
    "replay on current state"
);
var trace = SparseMergeTracer.Explain(
    Settings.Fragment.FragmentSchema,
    upstream.Build(),
    [
        new SparseContribution(0, Optional<object?>.Present(original)),
        new SparseContribution(1, Optional<object?>.Present(upstream.Build())),
    ]
);
Require(
    trace.Single(member => member.Name == "Label").ContributionIndices.SequenceEqual([1]),
    "domain-neutral merge trace"
);
Console.WriteLine("SparseFragments packed consumer passed.");

static void Require(bool condition, string capability)
{
    if (!condition)
        throw new InvalidOperationException("Failed: " + capability);
}

[SparseFragmentModel]
public partial class Settings
{
    public string? Label { get; set; }
    public Child? Child { get; set; }
}

public class Child
{
    public int Count { get; set; }
    public string Host { get; set; } = "localhost";
}
