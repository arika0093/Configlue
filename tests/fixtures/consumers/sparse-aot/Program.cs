using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SparseFragments;

// Standalone SparseFragments NativeAOT smoke (#317).
// Exercises only SparseFragments with no Configlue
// facade dependency: fragment construction/From, merge, diff/patch application,
// deep clone, and JSON Patch conversion through a source-generated
// JsonSerializerContext (the supported NativeAOT resolver path).

// Section 1: fragment construction / From.
var full = SparseAotSettings.Fragment.From(
    new SparseAotSettings { Label = "base", Count = 1 }
);
Require(full.Label.IsPresent && full.Label.Value == "base", "Fragment.From marks members present");
var sparse = new SparseAotSettings.Fragment { Label = "base" };
Require(!sparse.IsEmpty, "sparse construction");
Require(!sparse.Child.IsPresent, "sparse construction leaves members missing");

// Section 2: merge layered contributions (nested fragments merge member by member,
// Tags concatenates via Append).
var lower = SparseAotSettings.Fragment.From(
    new SparseAotSettings
    {
        Label = "base",
        Count = 1,
        Child = new SparseAotChild { Host = "db.local", Port = 5432 },
        Tags = ["base-plugin"],
    });
var higher = new SparseAotSettings.Fragment
{
    Child = new SparseAotChild.Fragment { Port = 9 },
    Tags = new List<string> { "extra-plugin" },
};
var merged = lower.Merge(higher).ToModel();
Require(merged.Label == "base", "merge keeps lower value for missing members");
Require(merged.Child!.Host == "db.local", "merge falls through nested missing members");
Require(merged.Child.Port == 9, "merge higher priority wins");
Require(merged.Tags.SequenceEqual(["base-plugin", "extra-plugin"]), "merge Append concatenates");

// Section 3: diff and typed patch application.
var before = new SparseAotSettings { Label = "before", Count = 1 };
var after = new SparseAotSettings { Label = "after", Count = 2 };
var diff = SparseAotSettings.Fragment.Diff(before, after);
var diffApplied = SparseAotSettings.Fragment.From(before).ApplyChanges(diff);
Require(diffApplied.Label.Value == "after", "Diff/ApplyChanges label");
Require(diffApplied.Count.Value == 2, "Diff/ApplyChanges count");

var original = SparseAotSettings.Fragment.From(
    new SparseAotSettings
    {
        Label = "original",
        Count = 7,
        Child = new SparseAotChild { Host = "keep", Port = 1 },
    });
var patch = new SparseAotSettings.Patch { Label = (string?)null };
patch.Child.Port = 9;
var updated = original.Apply(patch);
Require(updated.Label.IsPresent && updated.Label.Value is null, "typed patch present null");
Require(updated.Child.Value!.Port.Value == 9, "typed nested set");
Require(updated.Child.Value.Host.Value == "keep", "typed patch keeps unspecified members");
Require(original.Child.Value!.Port.Value == 1, "original fragment isolation");

var remove = new SparseAotSettings.Patch();
remove.Child.Unset();
Require(!original.Apply(remove).Child.IsPresent, "typed nested Unset");
var toNull = new SparseAotSettings.Patch();
toNull.Child.SetNull();
var nulled = original.Apply(toNull);
Require(nulled.Child.IsPresent && nulled.Child.Value is null, "typed nested SetNull");

// Section 4: build and deep clone.
var builder = original.ToBuilder();
builder.Label = Optional<string?>.Missing;
var edited = builder.Build();
Require(!edited.Label.IsPresent, "builder copy without member");
var clone = original.ToModel().DeepClone();
clone.Child!.Port = 42;
Require(original.ToModel().Child!.Port == 1, "DeepClone structural isolation");

// Section 5: RFC 6902 JSON Patch import/export through the supported
// source-generated resolver path.
var options = new JsonSerializerOptions { TypeInfoResolver = SparseAotJsonContext.Default };

// When reflection-based serialization is disabled (NativeAOT), the generated
// bridge must fail fast without a resolver instead of reaching runtime codegen.
if (!JsonSerializer.IsReflectionEnabledByDefault)
{
    var rejected = false;
    try
    {
        _ = SparseAotSettings.Patch.FromJsonPatch(
            Optional<SparseAotSettings.Fragment?>.Present(new SparseAotSettings.Fragment { Label = "base" }),
            Encoding.UTF8.GetBytes("""[{"op":"replace","path":"/Label","value":"patched"}]"""));
    }
    catch (InvalidOperationException exception)
    {
        rejected = exception.Message.Contains("TypeInfoResolver", StringComparison.Ordinal);
    }
    Require(rejected, "bridge rejected missing source-generated metadata");
}

var baseline = new SparseAotSettings.Fragment { Label = "base" };
var baselineOpt = Optional<SparseAotSettings.Fragment?>.Present(baseline);
var document = Encoding.UTF8.GetBytes("""[{"op":"replace","path":"/Label","value":"patched"}]""");
var jsonPatch = SparseAotSettings.Patch.FromJsonPatch(baselineOpt, document, options);
var updatedFromJson = baseline.Apply(jsonPatch);
Require(updatedFromJson.Label.Value == "patched", "JSON Patch import");

var exported = jsonPatch.ToJsonPatch(baselineOpt, options);
var exportedText = Encoding.UTF8.GetString(exported.ToArray());
Require(exportedText.Contains("/Label"), "JSON Patch export path");
Require(exportedText.Contains("patched"), "JSON Patch export value");

// Round-trip: re-importing the export onto the same baseline is semantically identical.
var reimported = SparseAotSettings.Patch.FromJsonPatch(baselineOpt, exported.ToArray(), options);
var viaOriginal = baseline.Apply(jsonPatch);
var viaExport = baseline.Apply(reimported);
Require(
    viaExport.Label.Value == viaOriginal.Label.Value,
    "JSON Patch export round-trip");

Console.WriteLine("SPARSE_AOT_SMOKE_PASS");

static void Require(bool condition, string capability)
{
    if (!condition)
    {
        throw new InvalidOperationException("Failed: " + capability);
    }
}

[SparseFragmentModel]
public partial class SparseAotSettings
{
    public string? Label { get; set; }

    public int Count { get; set; }

    public SparseAotChild? Child { get; set; }

    [SparseMerge(MergeMode.Append)]
    public List<string> Tags { get; set; } = [];
}

[SparseFragmentModel]
public partial class SparseAotChild
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; }
}

// Supported NativeAOT resolver path: scalar/collection member types used by the
// generated fragment converter resolve through this source-generated context.
// The fragment itself converts through the generated FragmentJsonConverter, so
// no JsonTypeInfo metadata is needed for the fragment type itself.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class SparseAotJsonContext : JsonSerializerContext;
