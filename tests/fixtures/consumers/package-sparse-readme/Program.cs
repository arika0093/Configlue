using System.Text;
using SparseFragments;

// Canonical compile-checked mirror of src/fragments/src/SparseFragments/README.md.
// Each block below corresponds to a README Usage section. Keep the model shapes
// (Settings/Child with [SparseFragmentModel]) in sync with the README so CI fails
// when the public generated API drifts from the documented samples.

// Section 3: the three states of Optional<T>.
Optional<string?> missing = Optional<string?>.Missing;
Optional<string?> present = "hello";
Optional<string?> explicitNull = Optional<string?>.Present(null);
Require(!missing.IsPresent, "Optional missing");
Require(present.IsPresent && present.Value == "hello", "Optional present");
Require(explicitNull.IsPresent && explicitNull.Value is null, "Optional present null");

// Section 4: sparse construction.
var full = Settings.Fragment.From(new Settings { Label = "base" });
Require(full.Label.IsPresent && full.Label.Value == "base", "Fragment.From marks members present");
var sparse = new Settings.Fragment { Label = "base" };
Require(!sparse.IsEmpty, "sparse construction");
Require(!sparse.Child.IsPresent, "sparse construction leaves members missing");

// Section 5: merge layered contributions (nested fragments merge member by member).
var lower = Settings.Fragment.From(
    new Settings
    {
        Label = "base",
        Child = new Child { Host = "db.local" },
        Plugins = ["base-plugin"],
    });
var higher = new Settings.Fragment
{
    Child = new Child.Fragment { Count = 9 },
    Plugins = new[] { "extra-plugin" },
};
var merged = lower.Merge(higher).ToModel();
Require(merged.Label == "base", "merge keeps lower value for missing members");
Require(merged.Child!.Host == "db.local", "merge falls through nested missing members");
Require(merged.Child.Count == 9, "merge higher priority wins");
Require(merged.Plugins.SequenceEqual(["base-plugin", "extra-plugin"]), "merge Append concatenates");

// Section 6: diff and typed patch.
var before = new Settings { Label = "before" };
var after = new Settings { Label = "after" };
var diff = Settings.Fragment.Diff(before, after);
var diffApplied = Settings.Fragment.From(before).ApplyChanges(diff);
Require(diffApplied.Label.Value == "after", "Diff/ApplyChanges");

var original = Settings.Fragment.From(
    new Settings
    {
        Label = "original",
        Child = new Child { Count = 7, Host = "keep" },
    });
var patch = new Settings.Patch { Label = (string?)null };
patch.Child.Count = 9;
var updated = original.Apply(patch);
Require(updated.Label.IsPresent && updated.Label.Value is null, "typed patch present null");
Require(updated.Child.Value!.Count.Value == 9, "typed nested set");
Require(updated.Child.Value.Host.Value == "keep", "typed patch keeps unspecified members");
Require(original.Child.Value!.Count.Value == 7, "original fragment isolation");

var remove = new Settings.Patch();
remove.Child.Unset();
Require(!original.Apply(remove).Child.IsPresent, "typed nested Unset");
var toNull = new Settings.Patch();
toNull.Child.SetNull();
var nulled = original.Apply(toNull);
Require(nulled.Child.IsPresent && nulled.Child.Value is null, "typed nested SetNull");

// Section 7: build and clone.
var builder = original.ToBuilder();
builder.Label = Optional<string?>.Missing;
var edited = builder.Build();
Require(!edited.Label.IsPresent, "builder copy without member");
var clone = original.ToModel().DeepClone();
clone.Child!.Count = 42;
Require(original.ToModel().Child!.Count == 7, "DeepClone structural isolation");

// Section 9.2/9.3: RFC 6902 JSON Patch import/export.
var baseline = new Settings.Fragment { Label = "base" };
var document = Encoding.UTF8.GetBytes("""[{"op":"replace","path":"/Label","value":"patched"}]""");
var jsonPatch = Settings.Patch.FromJsonPatch(
    Optional<Settings.Fragment?>.Present(baseline),
    document);
var updatedFromJson = baseline.Apply(jsonPatch);
Require(updatedFromJson.Label.Value == "patched", "JSON Patch import");

var baselineOpt = Optional<Settings.Fragment?>.Present(baseline);
var exported = jsonPatch.ToJsonPatch(baselineOpt);
var exportedText = Encoding.UTF8.GetString(exported.ToArray());
Require(exportedText.Contains("/Label"), "JSON Patch export");
Require(exportedText.Contains("patched"), "JSON Patch export value");

Console.WriteLine("SparseFragments README consumer passed.");

static void Require(bool condition, string capability)
{
    if (!condition)
    {
        throw new InvalidOperationException("Failed: " + capability);
    }
}

// Mirrors the README "Define your model" sample. Child must stay decorated with
// [SparseFragmentModel] because the merge sample constructs Child.Fragment directly.
[SparseFragmentModel]
public partial class Settings
{
    public string? Label { get; set; }
    public Child? Child { get; set; }

    [SparseMerge(MergeMode.Append)]
    public IReadOnlyList<string> Plugins { get; set; } = [];
}

[SparseFragmentModel]
public partial class Child
{
    public int Count { get; set; }
    public string Host { get; set; } = "localhost";
}
